using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Resolves configured connections for session-oriented agent transports.</summary>
public sealed class LlmAgentProviderService
{
    private readonly ILlmConnectionSource _connections;
    private readonly IReadOnlyDictionary<LlmTransport, ILlmAgentClient> _clients;

    public LlmAgentProviderService(
        ILlmConnectionSource connections,
        IEnumerable<ILlmAgentClient> clients)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(clients);
        _connections = connections;
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

    public async Task<ILlmAgentSession> StartSessionAsync(
        string connectionId,
        LlmAgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            _connections,
            connectionId,
            cancellationToken);
        ILlmAgentClient client = ClientFor(connection.Transport);
        if (client is IConnectionBoundAgentClient bound)
        {
            return await bound.StartSessionAsync(connectionId, connection, options, cancellationToken);
        }
        return await client.StartSessionAsync(
            connection,
            options,
            cancellationToken);
    }

    public async Task<ILlmAgentSession> ResumeSessionAsync(
        string connectionId,
        string threadId,
        LlmAgentResumeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            _connections,
            connectionId,
            cancellationToken);
        return await ClientFor(connection.Transport).ResumeSessionAsync(
            connection,
            threadId,
            options,
            cancellationToken);
    }

    public async Task<ILlmAgentSession> ForkSessionAsync(
        string connectionId,
        string threadId,
        string? lastTurnId = null,
        LlmAgentResumeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            _connections,
            connectionId,
            cancellationToken);
        return await ClientFor(connection.Transport).ForkSessionAsync(
            connection,
            threadId,
            lastTurnId,
            options,
            cancellationToken);
    }

    private ILlmAgentClient ClientFor(LlmTransport transport) =>
        _clients.TryGetValue(transport, out ILlmAgentClient? client)
            ? client
            : throw new NotSupportedException(
                $"No LLM agent client is registered for transport {transport}.");
}
