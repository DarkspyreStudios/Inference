namespace Darkspyre.Inference.Connect.LLM;

public interface ILlmEmbeddingClient
{
    bool Supports(LlmTransport transport);

    Task<LlmEmbeddingResult> EmbedAsync(
        LlmConnection connection,
        LlmEmbeddingRequest request,
        CancellationToken cancellationToken = default);
}
