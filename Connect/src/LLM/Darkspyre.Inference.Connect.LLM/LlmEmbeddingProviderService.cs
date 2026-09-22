using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Routes embeddings through the one explicitly configured provider connection.</summary>
public sealed class LlmEmbeddingProviderService
{
    private readonly ILlmConnectionSource _connections;
    private readonly IReadOnlyList<ILlmEmbeddingClient> _clients;

    public LlmEmbeddingProviderService(
        ILlmConnectionSource connections,
        IEnumerable<ILlmEmbeddingClient> clients)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _clients = [.. clients];
    }

    public async Task<LlmEmbeddingResult> EmbedAsync(
        string connectionId,
        LlmEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Inputs.Count == 0 || request.Inputs.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty embedding input is required.", nameof(request));
        }

        LlmConnection connection = await LlmConnectionResolver.ResolveAsync(
            _connections,
            connectionId,
            cancellationToken);
        ILlmEmbeddingClient client = _clients.SingleOrDefault(value => value.Supports(connection.Transport))
            ?? throw new NotSupportedException(
                $"The configured {connection.Transport} provider does not support embeddings in this client.");
        return await client.EmbedAsync(connection, request, cancellationToken);
    }
}
