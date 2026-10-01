namespace Darkspyre.Inference.Connect.LLM;

public interface ILlmEmbeddingClient
{
    bool Supports(LlmTransport transport);

    /// <summary>Validates the request without credentials, refresh, discovery or network effects.</summary>
    void ValidateRequest(LlmConnection connection, LlmEmbeddingRequest request);

    Task<LlmEmbeddingResult> EmbedAsync(
        LlmConnection connection,
        LlmEmbeddingRequest request,
        CancellationToken cancellationToken = default);
}
