using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Resolves configured connections for session-oriented agent transports.</summary>
public sealed class LlmAgentProviderService : IDisposable, IAsyncDisposable
{
    private ILlmConnectionSource? _connections;
    private ILlmConnectionSource Connections => _connections ?? throw new ObjectDisposedException(nameof(LlmAgentProviderService));
    private readonly Dictionary<LlmTransport, ILlmAgentClient> _clients;
    private readonly LlmAccountUsageObservations _observations;
    private readonly bool _ownsObservations;
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly Lock _gate = new();
    private readonly HashSet<OwnedLlmAgentSession> _sessions = [];
    private bool _retiring;
    private TaskCompletionSource? _retirement;

    internal LlmAgentProviderService(
        ILlmConnectionSource connections,
        IEnumerable<ILlmAgentClient> clients,
        LlmAccountUsageObservations? observations = null)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clients);
        _connections = connections;
        _observations = observations ?? new();
        _ownsObservations = observations is null;
        ILlmAgentClient[] clientArray = [.. clients];
        IGrouping<LlmTransport, ILlmAgentClient>? duplicate = clientArray
            .GroupBy(client => client.Transport)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Multiple LLM agent clients are registered for transport {duplicate.Key}.");
        }

        _clients = clientArray.ToDictionary(client => client.Transport);
        SupportedTransports = [.. _clients.Keys.OrderBy(transport => transport)];
    }

    public IReadOnlyList<LlmTransport> SupportedTransports { get; }

    /// <summary>Runtime login is unavailable until an adapter implements its complete owned lifecycle.</summary>
    public ILlmAgentRuntimeAuthentication BindRuntimeAuthentication(string connectionId)
    {
        using var operation = Enter(CancellationToken.None);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        throw new NotSupportedException("No runtime authentication adapter is available.");
    }

    /// <summary>Validates an explicit draft without resolving, refreshing, launching or signing in.</summary>
    public ILlmAgentRuntimeAuthentication BindRuntimeAuthentication(LlmConnection draft, SetupConfigurationIdentity identity)
    {
        using var operation = Enter(CancellationToken.None);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        _ = RuntimeAuthData.Transport(draft.Transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Version);
        _ = CallerData.Path(draft.Endpoint);
        throw new NotSupportedException("No runtime authentication adapter is available.");
    }

    internal void ValidateSource(ILlmConnectionSource connections)
    {
        using var operation = Enter(CancellationToken.None);
        if (!ReferenceEquals(Connections, connections))
            throw new ArgumentException("The agent facade must borrow the same connection source.", nameof(connections));
    }

    public async Task<ILlmAgentSession> StartSessionAsync(
        string connectionId,
        LlmAgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = Enter(cancellationToken);
        cancellationToken = operation.Token;
        options = ClientTools.Snapshot(options);
        LlmAccountUsageObservations.Reservation reservation = _observations.Reserve(connectionId);
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            Connections,
            connectionId,
            cancellationToken);
        connection = connection with { AccountUsageObservationBinding = reservation.Bind(connection) };
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOpeningCanceled(cancellationToken);
        RuntimeInputAdmission.Validate(connection);
        ILlmAgentSession session = await ClientFor(connection.Transport).StartSessionAsync(
            connectionId,
            connection,
            options,
            cancellationToken);
        return await OwnAsync(session, cancellationToken);
    }

    /// <summary>Reopens a provider thread by its ID. No built transport resumes one; a conversation continues by
    /// <see cref="StartSessionAsync"/> with its history.</summary>
    public async Task<ILlmAgentSession> ResumeSessionAsync(
        string connectionId,
        string threadId,
        LlmAgentResumeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = Enter(cancellationToken);
        cancellationToken = operation.Token;
        options = options is null ? null : ClientTools.Snapshot(options);
        LlmAccountUsageObservations.Reservation reservation = _observations.Reserve(connectionId);
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            Connections,
            connectionId,
            cancellationToken);
        connection = connection with { AccountUsageObservationBinding = reservation.Bind(connection) };
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOpeningCanceled(cancellationToken);
        RuntimeInputAdmission.Validate(connection);
        _ = ClientFor(connection.Transport);
        throw new NotSupportedException($"{connection.Transport} agent sessions cannot resume a provider thread.");
    }

    private void ThrowIfOpeningCanceled(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error)
        { throw new OperationCanceledException("Agent opening was canceled by retirement.", error, token); }
    }

    private LlmOperationLifetime.Lease Enter(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retiring, this);
            return _lifetime.Enter(cancellationToken);
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "Canceled session opening preserves its actual cleanup failure and cancellation token.")]
    private async Task<ILlmAgentSession> OwnAsync(ILlmAgentSession session, CancellationToken cancellationToken)
    {
        var owned = new OwnedLlmAgentSession(session, Forget);
        lock (_gate)
        {
            _sessions.Add(owned);
            if (!_retiring && !cancellationToken.IsCancellationRequested)
            {
                return owned;
            }
        }
        try { await owned.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error)
        { throw new OperationCanceledException("Agent opening was canceled during session cleanup.", error, cancellationToken); }
        cancellationToken.ThrowIfCancellationRequested();
        throw new ObjectDisposedException(nameof(LlmAgentProviderService));
    }

    private void Forget(OwnedLlmAgentSession session)
    {
        lock (_gate) { _sessions.Remove(session); }
    }

    /// <summary>Cancels and drains session creation and retires all returned sessions. Supplied dependencies remain borrowed.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        OwnedLlmAgentSession[] sessions;
        lock (_gate)
        {
            if (_retirement is not null) return new(_retirement.Task);
            _retiring = true;
            _retirement = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            sessions = [.. _sessions];
        }
        _ = RetireAsync(sessions, completion);
        return new(completion.Task);
    }

    [SuppressMessage("Design", "CA1031", Justification = "The shared retirement completion preserves adapter failures for every disposal caller.")]
    private async Task RetireAsync(OwnedLlmAgentSession[] sessions, TaskCompletionSource completion)
    {
        try
        {
            Task openings = _lifetime.RetireAsync();
            Task retiredSessions = Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask()));
            Task initial = Task.WhenAll(openings, retiredSessions);
            await initial.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            // Openings can return a refused cleanup owner after the initial snapshot.
            lock (_gate) { sessions = [.. _sessions]; }
            Task retirement = Task.WhenAll(initial, Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask())));
            try { await retirement.ConfigureAwait(false); }
            catch
            {
                if (retirement.Exception is { InnerExceptions.Count: > 1 } failures)
                {
                    Exception[] distinct = failures.Flatten().InnerExceptions
                        .Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
                    if (distinct.Length == 1) ExceptionDispatchInfo.Throw(distinct[0]);
                    throw new AggregateException(distinct);
                }
                throw;
            }
            finally
            {
                _clients.Clear();
                _connections = null;
                if (_ownsObservations) { _observations.Dispose(); }
            }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    /// <summary>Synchronously retires the facade. Use asynchronous disposal while sessions or provider work are active.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private ILlmAgentClient ClientFor(LlmTransport transport) =>
        _clients.TryGetValue(transport, out ILlmAgentClient? client)
            ? client
            : throw new NotSupportedException(
                $"No LLM agent client is registered for transport {transport}.");
}
