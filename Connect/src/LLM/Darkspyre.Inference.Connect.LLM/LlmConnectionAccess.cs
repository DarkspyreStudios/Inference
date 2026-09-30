using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Owns current credential acquisition and version-conditional refresh for a connection source.</summary>
public sealed class LlmConnectionAccess : IDisposable, IAsyncDisposable
{
    private readonly ILlmConnectionSource _connections;
    private readonly Dictionary<LlmTransport, ILlmAuthenticator> _authenticators;
    private readonly HashSet<LlmTransport> _ignoresStoredCredential;
    private readonly LlmOperationLifetime _lifetime = new();

    public LlmConnectionAccess(ILlmConnectionSource connections, IEnumerable<ILlmAuthenticator> authenticators,
        IEnumerable<ILlmClient>? clients = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        ArgumentNullException.ThrowIfNull(authenticators);
        _authenticators = authenticators.ToDictionary(authenticator => authenticator.Transport);
        _ignoresStoredCredential = (clients ?? []).Where(client => client is IIgnoresStoredCredential)
            .Select(client => client.Transport).ToHashSet();
    }

    /// <summary>Resolves a connection and saves a refreshed OAuth credential against its expected version.</summary>
    public async Task<LlmConnection> AcquireAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(_connections, connectionId, operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        return await RefreshCoreAsync(connectionId, connection, operation.Token);
    }

    internal bool UsesSource(ILlmConnectionSource source) => ReferenceEquals(source, _connections);

    internal async Task<LlmConnection> RefreshAsync(string connectionId, LlmConnection connection, CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        return await RefreshCoreAsync(connectionId, connection, operation.Token);
    }

    private async Task<LlmConnection> RefreshCoreAsync(string connectionId, LlmConnection connection, CancellationToken cancellationToken)
    {
        if (_ignoresStoredCredential.Contains(connection.Transport)) return connection;
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if (credential?.OAuth is null || !_authenticators.TryGetValue(connection.Transport, out ILlmAuthenticator? authenticator))
            return connection;

        LlmCredential fresh = await authenticator.EnsureFreshAsync(credential, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (fresh == credential) return connection;
        string json = fresh.Serialize();
        await _connections.SaveCredentialAsync(connectionId, json, connection.Version, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return connection with { Credential = json };
    }

    /// <summary>Fences acquisition, cancels and drains resolution/refresh/save. Dependencies remain borrowed.</summary>
    public ValueTask DisposeAsync() => new(_lifetime.RetireAsync(() =>
    {
        _authenticators.Clear();
        _ignoresStoredCredential.Clear();
        return Task.CompletedTask;
    }));

    /// <summary>Synchronously retires acquisition. Use asynchronous disposal while work is active.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
