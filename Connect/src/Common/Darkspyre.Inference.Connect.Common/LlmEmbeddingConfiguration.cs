namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Embedding protocol options for a connection whose endpoint serves an extended OpenAI embeddings API. Both are off by
/// default, and the request is then the standard one.
/// </summary>
public sealed record LlmEmbeddingConfiguration
{
    /// <summary>
    /// True sends <c>"input_type": "query"</c> or <c>"document"</c> from the request's kind, and a document input's title
    /// as its own <c>"title"</c> in a content item, so the endpoint formats each input for its model.
    /// </summary>
    public bool SendsInputType { get; init; }

    /// <summary>
    /// True sends an image input as a content item with a base64 data URL. False refuses image inputs before any request.
    /// </summary>
    public bool AcceptsImages { get; init; }
}
