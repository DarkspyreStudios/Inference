namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>An input-token estimate, and whether part of it is the provider's own report for earlier history.</summary>
internal readonly record struct ContextEstimate(long Tokens, bool IncludesProviderReport = false);

internal interface ILlmContextEstimator
{
    /// <summary>Validates the same adapter projection used for generation, without I/O.</summary>
    ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request);

    /// <summary>Safe text that qualifies every estimate from this adapter, or null.</summary>
    string? EstimateDiagnostic { get; }
}
