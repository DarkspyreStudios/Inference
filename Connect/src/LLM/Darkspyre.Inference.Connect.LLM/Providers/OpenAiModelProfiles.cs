using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// The reasoning levels and sampling rule of one OpenAI Responses model. <paramref name="Verified"/> is false for a
/// model without a profile row; it keeps the adapter's former default levels and accepts temperature at any level.
/// </summary>
internal sealed record OpenAiModelProfile(IReadOnlySet<LlmReasoningLevel> ReasoningLevels, bool Verified = true,
    InferenceSupport SupportsPromptCacheOptions = InferenceSupport.Unknown)
{
    public bool IsReasoning => ReasoningLevels.Count > 0;

    /// <summary>Non-reasoning models accept sampling always; verified reasoning models only at effort none.</summary>
    public bool AllowsSampling => !Verified || !IsReasoning || ReasoningLevels.Contains(LlmReasoningLevel.None);

    /// <summary>True when temperature is accepted only with <see cref="LlmReasoningLevel.None"/>.</summary>
    public bool SamplingRequiresNoReasoning => Verified && IsReasoning;
}

/// <summary>
/// Per-model OpenAI Responses profiles. The Models API reports no capabilities, so the profiles are a table. Each row
/// cites its model page.
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
            LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum), SupportsPromptCacheOptions: InferenceSupport.Supported)),
        // https://developers.openai.com/api/docs/guides/reasoning: rejects none.
        ("gpt-6-astra", new(LlmReasoningEffort.Levels(
            LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium,
            LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum), SupportsPromptCacheOptions: InferenceSupport.Supported)),
        // https://developers.openai.com/api/docs/guides/reasoning: accepts none and low to max.
        ("gpt-6-luna", new(LlmReasoningEffort.Levels(
            LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium,
            LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum), SupportsPromptCacheOptions: InferenceSupport.Supported)),
        // https://developers.openai.com/api/docs/guides/reasoning: accepts every value.
        ("gpt-5.6", new(Every, SupportsPromptCacheOptions: InferenceSupport.Supported)),
        // https://developers.openai.com/api/docs/guides/reasoning: accepts every value.
        ("gpt-5.5", new(Every)),
        // https://developers.openai.com/api/docs/models/gpt-4.1: a non-reasoning model.
        ("gpt-4.1", new(LlmReasoningEffort.Levels())),
        // https://developers.openai.com/api/docs/models/gpt-4o: a non-reasoning model; the page lists no effort.
        ("gpt-4o", new(LlmReasoningEffort.Levels()))
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

    /// <summary>
    /// Rejects a temperature on a verified reasoning model unless the request sets <see cref="LlmReasoningLevel.None"/>.
    /// A request without a level counts as reasoning, because these models default to effort medium.
    /// </summary>
    public static void ValidateSampling(LlmChatRequest request)
    {
        if ((request.Temperature is not null || request.TopP is not null)
            && request.ReasoningLevel != LlmReasoningLevel.None
            && Get(request.ModelId).SamplingRequiresNoReasoning)
        {
            throw new NotSupportedException("This model accepts temperature and top-p only with ReasoningLevel None.");
        }
    }
}
