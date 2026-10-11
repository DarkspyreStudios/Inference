using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// The reasoning levels, sampling rule and published limits of one OpenAI API model. <paramref name="Verified"/> is
/// false for a model without a profile row; it keeps the adapter's former default levels and accepts temperature at
/// any level. <paramref name="ContextWindow"/> and <paramref name="MaxOutputTokens"/> are the model page's limits.
/// </summary>
internal sealed record OpenAiModelProfile(IReadOnlySet<LlmReasoningLevel> ReasoningLevels, bool Verified = true,
    InferenceSupport SupportsPromptCacheOptions = InferenceSupport.Unknown, long? ContextWindow = null,
    long? MaxOutputTokens = null)
{
    /// <summary>A level above None is offered.</summary>
    public bool IsReasoning => ReasoningLevels.Any(level => level > LlmReasoningLevel.None);

    /// <summary>Non-reasoning models accept sampling always; verified reasoning models only at effort none.</summary>
    public bool AllowsSampling => !Verified || !IsReasoning || ReasoningLevels.Contains(LlmReasoningLevel.None);

    /// <summary>True when temperature is accepted only with <see cref="LlmReasoningLevel.None"/>.</summary>
    public bool SamplingRequiresNoReasoning => Verified && IsReasoning;
}

/// <summary>
/// Per-model OpenAI API profiles, read only on OpenAI's own endpoint. The Models API reports no capabilities or
/// limits, so the profiles are a table. Each row cites its pages: the model page for the levels and limits
/// (https://developers.openai.com/api/docs/models), read 2026-10-11.
/// </summary>
internal static class OpenAiModelProfiles
{
    private static readonly IReadOnlySet<LlmReasoningLevel> Every = LlmReasoningEffort.Levels(
        LlmReasoningLevel.None, LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium,
        LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum);

    /// <summary>A model without a row keeps these levels and accepts temperature.</summary>
    internal static readonly OpenAiModelProfile Unverified = new(
        LlmReasoningEffort.Levels(
            LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium,
            LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh),
        Verified: false);

    // Longest prefix first, so a longer, more specific prefix is tested before a shorter one it contains.
    private static readonly (string Prefix, OpenAiModelProfile Profile)[] Rows =
    [
        // https://developers.openai.com/api/docs/guides/latest-model: rejects none and minimal.
        ("gpt-6.1-sol", new(LlmReasoningEffort.Levels(
            LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High,
            LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum), SupportsPromptCacheOptions: InferenceSupport.Supported,
            ContextWindow: 1_050_000, MaxOutputTokens: 128_000)),
        // https://developers.openai.com/api/docs/guides/reasoning: rejects none.
        ("gpt-6-astra", new(LlmReasoningEffort.Levels(
            LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium,
            LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum), SupportsPromptCacheOptions: InferenceSupport.Supported,
            ContextWindow: 1_050_000, MaxOutputTokens: 128_000)),
        // https://developers.openai.com/api/docs/guides/reasoning: accepts none and low to max.
        ("gpt-6-luna", new(LlmReasoningEffort.Levels(
            LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium,
            LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum), SupportsPromptCacheOptions: InferenceSupport.Supported,
            ContextWindow: 1_050_000, MaxOutputTokens: 128_000)),
        // https://developers.openai.com/api/docs/guides/reasoning: accepts every value; /api/docs/models/gpt-5.6.
        ("gpt-5.6", new(Every, SupportsPromptCacheOptions: InferenceSupport.Supported,
            ContextWindow: 1_050_000, MaxOutputTokens: 128_000)),
        // https://developers.openai.com/api/docs/guides/reasoning: accepts every value; /api/docs/models/gpt-5.5.
        ("gpt-5.5", new(Every, ContextWindow: 1_050_000, MaxOutputTokens: 128_000)),
        // https://developers.openai.com/api/docs/models/gpt-4.1: a non-reasoning model.
        ("gpt-4.1", new(LlmReasoningEffort.Levels(LlmReasoningLevel.None), ContextWindow: 1_047_576, MaxOutputTokens: 32_768)),
        // https://developers.openai.com/api/docs/models/gpt-4o: a non-reasoning model; the page lists no effort.
        ("gpt-4o", new(LlmReasoningEffort.Levels(LlmReasoningLevel.None), ContextWindow: 128_000, MaxOutputTokens: 16_384))
    ];

    public static OpenAiModelProfile Get(string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            foreach ((string prefix, OpenAiModelProfile profile) in Rows)
            {
                if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }
        }

        return Unverified;
    }
}
