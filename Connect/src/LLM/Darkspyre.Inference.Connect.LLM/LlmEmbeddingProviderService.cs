using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

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
    private LlmProviderService? _providerService;
    private readonly LlmCleanupOutcomes _cleanupOutcomes = new();
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly Lock _disposeGate = new();
    private Task? _disposal;

    public LlmEmbeddingProviderService(
        ILlmConnectionSource connections,
        IEnumerable<ILlmEmbeddingClient> clients,
        IEnumerable<ILlmAuthenticator>? authenticators = null,
        LlmConnectionAccess? connectionAccess = null,
        LlmProviderService? providerService = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        ArgumentNullException.ThrowIfNull(clients);
        if (connectionAccess is not null && !connectionAccess.UsesSource(connections))
            throw new ArgumentException("Credential access must use the facade's connection source.", nameof(connectionAccess));
        if (providerService is not null)
        {
            if (connectionAccess is null)
                throw new ArgumentException("A catalog facade requires explicitly shared credential access.", nameof(connectionAccess));
            providerService.ValidateXaiAccess(connectionAccess);
        }
        _clients = [.. clients];
        _connectionAccess = connectionAccess ?? new LlmConnectionAccess(connections, authenticators ?? []);
        _ownsConnectionAccess = connectionAccess is null;
        _providerService = providerService;
    }

    public async Task<LlmEmbeddingResult> EmbedAsync(
        string connectionId,
        LlmEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        try
        {
            request = EmbeddingRequestData.Snapshot(request);
            ThrowIfRequestCanceled(cancellationToken);
            if (request.Inputs.Count == 0 || request.Inputs.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("At least one non-empty embedding input is required.", nameof(request));

            LlmConnection connection = await ResolveAsync(connectionId, cancellationToken).ConfigureAwait(false);
            ILlmEmbeddingClient client = ClientFor(connection.Transport);
            ThrowIfRequestCanceled(cancellationToken);
            client.ValidateRequest(connection, request);
            ThrowIfRequestCanceled(cancellationToken);
            LlmProviderService? catalog = connection.Transport == LlmTransport.XaiResponses ? RequireCatalogService() : null;
            ThrowIfRequestCanceled(cancellationToken);
            connection = await ConnectionAccess.RefreshAsync(connectionId, connection,
                current => client.ValidateRequest(current, request), () => ThrowIfRequestCanceled(cancellationToken), cancellationToken).ConfigureAwait(false);
            ThrowIfRequestCanceled(cancellationToken);
            if (catalog is not null)
            {
                IReadOnlyList<LlmModel> models = await catalog.GetXaiCatalogSnapshotAsync(connectionId,
                    connection, ModelCatalogFamily.Embedding, () => ThrowIfRequestCanceled(cancellationToken), cancellationToken).ConfigureAwait(false);
                ThrowIfRequestCanceled(cancellationToken);
                connection = connection with { Catalog = models };
            }
            LlmEmbeddingResult result = client is XaiEmbeddingClient xai
                ? await xai.EmbedAsync(connection, request, () => ThrowIfRequestCanceled(cancellationToken), cancellationToken).ConfigureAwait(false)
                : await client.EmbedAsync(connection, request, cancellationToken).ConfigureAwait(false);
            ThrowIfRequestCanceled(cancellationToken);
            return result;
        }
        catch (Exception error) { _cleanupOutcomes.Record(error); throw; }
    }

    /// <summary>Lists observed xAI embedding models through the explicitly shared LLM catalog owner.</summary>
    public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(string connectionId,
        CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease operation = _lifetime.Enter(cancellationToken);
        cancellationToken = operation.Token;
        try
        {
            LlmProviderService catalog = RequireCatalogService();
            ThrowIfRequestCanceled(cancellationToken);
            LlmConnection connection = await ResolveAsync(connectionId, cancellationToken).ConfigureAwait(false);
            ValidateCatalogConnection(connection);
            _ = ClientFor(connection.Transport);
            ThrowIfRequestCanceled(cancellationToken);
            connection = await ConnectionAccess.RefreshAsync(connectionId, connection, ValidateCatalogConnection,
                () => ThrowIfRequestCanceled(cancellationToken), cancellationToken).ConfigureAwait(false);
            ThrowIfRequestCanceled(cancellationToken);
            IReadOnlyList<LlmModel> models = await catalog.GetXaiCatalogSnapshotAsync(connectionId, connection,
                ModelCatalogFamily.Embedding, () => ThrowIfRequestCanceled(cancellationToken), cancellationToken).ConfigureAwait(false);
            ThrowIfRequestCanceled(cancellationToken);
            return models;
        }
        catch (Exception error) { _cleanupOutcomes.Record(error); throw; }
    }

    private async Task<LlmConnection> ResolveAsync(string connectionId, CancellationToken token)
    {
        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(Connections, connectionId, token).ConfigureAwait(false);
        ThrowIfRequestCanceled(token);
        RuntimeInputAdmission.Validate(connection);
        connection = EmbeddingRequestData.Snapshot(connection);
        ThrowIfRequestCanceled(token);
        return connection;
    }

    private ILlmEmbeddingClient ClientFor(LlmTransport transport) =>
        _clients.SingleOrDefault(value => value.Supports(transport))
        ?? throw new NotSupportedException($"The configured {transport} provider does not support embeddings in this client.");

    private LlmProviderService RequireCatalogService() => _providerService?.SupportsXaiSnapshot == true
        ? _providerService : throw new NotSupportedException("xAI embedding catalog operations require the explicitly shared LLM facade.");

    private static void ValidateCatalogConnection(LlmConnection connection)
    {
        if (connection.Transport != LlmTransport.XaiResponses)
            throw new NotSupportedException("Shared embedding listing is implemented only for the xAI transport.");
        ProviderHttp.ValidateAdditionalHeaders(connection.AdditionalHeaders);
        XaiFileRequestData.Connection(connection);
    }

    private void ThrowIfRequestCanceled(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Linked cancellation callbacks can lag behind the synchronous admission fence.
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error)
        {
            throw new OperationCanceledException("Embedding request was canceled by retirement.", error, token);
        }
    }

    /// <summary>Fences new requests, cancels and awaits active embedding work. Supplied dependencies remain borrowed.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) { return new(_disposal ??= RetireAsync()); }
    }

    private async Task RetireAsync()
    {
        Task operations = _lifetime.RetireAsync(_cleanupOutcomes.ReleaseAsync);
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
            _providerService = null;
        }
    }

    /// <summary>Synchronously retires the facade. Use asynchronous disposal while provider work is active.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
