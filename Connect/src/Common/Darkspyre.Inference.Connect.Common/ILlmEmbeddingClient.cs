namespace Darkspyre.Inference.Connect.LLM;

/// <summary>A transport-specific client that embeds text through an LLM connection.</summary>
public interface ILlmEmbeddingClient
{
    /// <summary>Whether this client serves connections of <paramref name="transport"/>.</summary>
    bool Supports(LlmTransport transport);

    /// <summary>Validates the request without credentials, refresh, discovery or network effects.</summary>
    void ValidateRequest(LlmConnection connection, LlmEmbeddingRequest request);

    /// <summary>Embeds the request's inputs through the connection and returns the vectors.</summary>
    Task<LlmEmbeddingResult> EmbedAsync(
        LlmConnection connection,
        LlmEmbeddingRequest request,
        CancellationToken cancellationToken = default);
}
