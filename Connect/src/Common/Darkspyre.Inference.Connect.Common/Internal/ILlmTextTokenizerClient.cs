namespace Darkspyre.Inference.Connect.LLM.Internal;

internal interface ILlmTextTokenizerClient
{
    Task<LlmTokenization> TokenizeAsync(LlmConnection connection, string modelId, string text,
        CancellationToken cancellationToken = default);
}
