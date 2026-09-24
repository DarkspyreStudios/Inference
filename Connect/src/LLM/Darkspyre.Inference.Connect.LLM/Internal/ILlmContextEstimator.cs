namespace Darkspyre.Inference.Connect.LLM.Internal;

internal interface ILlmContextEstimator
{
    /// <summary>Validates the same adapter projection used for generation, without I/O.</summary>
    long? EstimateInputTokens(LlmConnection connection, LlmChatRequest request);

    /// <summary>Safe text that qualifies every estimate from this adapter, or null.</summary>
    string? EstimateDiagnostic { get; }
}
