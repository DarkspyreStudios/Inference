using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Routes embeddings through the one explicitly configured provider connection.</summary>
public sealed class LlmEmbeddingProviderService : IDisposable, IAsyncDisposable
{
    private ILlmConnectionSource? _connections;
    private ILlmConnectionSource Connections => _connections ?? throw new ObjectDisposedException(nameof(LlmEmbeddingProviderService));
    private readonly List<ILlmEmbeddingClient> _clients;
    private LlmConnectionAccess? _connectionAccess;
    private LlmConnectionAccess ConnectionAccess => _connectionAccess ?? throw new ObjectDisposedException(nameof(LlmEmbeddingProviderService));
    private readonly bool _ownsConnectionAccess;
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly Lock _disposeGate = new();
    private Task? _disposal;

    public LlmEmbeddingProviderService(
        ILlmConnectionSource connections,
        IEnumerable<ILlmEmbeddingClient> clients,
        IEnumerable<ILlmAuthenticator>? authenticators = null,
        LlmConnectionAccess? connectionAccess = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        ArgumentNullException.ThrowIfNull(clients);
        if (connectionAccess is not null && !connectionAccess.UsesSource(connections))
            throw new ArgumentException("Credential access must use the facade's connection source.", nameof(connectionAccess));
        _clients = [.. clients];
        _connectionAccess = connectionAccess ?? new LlmConnectionAccess(connections, authenticators ?? []);
        _ownsConnectionAccess = connectionAccess is null;
    }

    public async Task<LlmEmbeddingResult> EmbedAsync(
        string connectionId,
        LlmEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        ArgumentNullException.ThrowIfNull(request);
        if (request.Inputs.Count == 0 || request.Inputs.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty embedding input is required.", nameof(request));
        }

        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            Connections,
            connectionId,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ILlmEmbeddingClient client = _clients.SingleOrDefault(value => value.Supports(connection.Transport))
            ?? throw new NotSupportedException(
                $"The configured {connection.Transport} provider does not support embeddings in this client.");
        connection = await ConnectionAccess.RefreshAsync(connectionId, connection, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        LlmEmbeddingResult result = await client.EmbedAsync(connection, request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Fences new requests, cancels and awaits active embedding work. Supplied dependencies remain borrowed.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) { return new(_disposal ??= RetireAsync()); }
    }

    private async Task RetireAsync()
    {
        Task operations = _lifetime.RetireAsync();
        Task credentials = _ownsConnectionAccess ? ConnectionAccess.DisposeAsync().AsTask() : Task.CompletedTask;
        Task retirement = Task.WhenAll(operations, credentials);
        try { await retirement.ConfigureAwait(false); }
        catch
        {
            if (retirement.Exception is { InnerExceptions.Count: > 1 } failures) throw failures;
            throw;
        }
        finally
        {
            _clients.Clear();
            _connections = null;
            _connectionAccess = null;
        }
    }

    /// <summary>Synchronously retires the facade. Use asynchronous disposal while provider work is active.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
