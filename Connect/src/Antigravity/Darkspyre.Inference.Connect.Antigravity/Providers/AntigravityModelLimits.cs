using System.Collections.Frozen;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// The context window and output limit of each model <c>agy models</c> lists (AGY 1.4.0, 2026-10-16). AGY reports
/// neither, so the values are the model owners' published limits.
/// </summary>
internal static class AntigravityModelLimits
{
    // Sources:
    // [G] Gemini 3.8 Flash: https://ai.google.dev/gemini-api/docs/models/gemini-3.8-flash
    //     Input token limit 1,048,576; output token limit 65,536. The -low, -medium and -high slugs are one model at
    //     three thinking levels.
    // [C] Anthropic, "Context window sizes by model":
    //     https://platform.claude.com/docs/en/build-with-claude/context-windows#context-window-sizes-by-model
    //     Opus 4.6 and Sonnet 4.6 have a 1M-token window and generate up to 128K output tokens; the 1M window is also
    //     available on Google Cloud Vertex AI (https://claude.com/blog/1m-context-ga).
    // [V] OpenAI gpt-oss 120B on Vertex AI: https://docs.cloud.google.com/vertex-ai/generative-ai/docs/maas/openai/gpt-oss-120b
    //     Context length 131,072. Its stated output limit equals the window, which as a reserve leaves no input room,
    //     so no output limit is recorded and the default reserve applies.
    private static readonly FrozenDictionary<string, (long ContextTokens, long? OutputTokens)> Models =
        new Dictionary<string, (long, long?)>
        {
            ["gemini-3.8-flash-high"] = (1_048_576, 65_536), // [G]
            ["gemini-3.8-flash-medium"] = (1_048_576, 65_536), // [G]
            ["gemini-3.8-flash-low"] = (1_048_576, 65_536), // [G]
            ["claude-sonnet-4-6"] = (1_000_000, 128_000), // [C]
            ["claude-opus-4-6-thinking"] = (1_000_000, 128_000), // [C]
            ["gpt-oss-120b-medium"] = (131_072, null) // [V]
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The model with its window and output limit. An unlisted model, <c>auto</c> included, has neither, so it never
    /// reaches a compaction trigger, as an unlisted Claude model does not.
    /// </summary>
    public static LlmModel Get(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        return Models.TryGetValue(modelId, out var limit)
            ? new(modelId, modelId) { MaxContextTokens = limit.ContextTokens, MaxOutputTokens = limit.OutputTokens }
            : new(modelId, modelId);
    }
}
