using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// An input-token estimate, and whether part of it is the provider's own report for earlier history.
/// <paramref name="EntirelyReported"/> is true when the provider's report is the whole count and nothing is estimated.
/// </summary>
internal readonly record struct ContextEstimate(long Tokens, bool IncludesProviderReport = false, bool EntirelyReported = false);

internal interface ILlmContextEstimator
{
    /// <summary>
    /// Validates the same adapter projection used for generation and estimates its input tokens. Caller-owned resources
    /// are counted by their descriptors in <paramref name="resources"/>, which the caller resolves first
    /// (<see cref="ResourceSizes"/>). An adapter may ask the model service to count text; a failed count uses the local
    /// estimate for that text.
    /// </summary>
    ValueTask<ContextEstimate?> EstimateAsync(LlmConnection connection, LlmChatRequest request,
        IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> resources,
        CancellationToken cancellationToken);

    /// <summary>Safe text that qualifies every estimate from this adapter, or null.</summary>
    string? EstimateDiagnostic { get; }
}
