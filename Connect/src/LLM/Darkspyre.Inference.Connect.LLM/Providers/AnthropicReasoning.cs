using System.Collections.Frozen;
using System.Text.Json.Nodes;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Anthropic Messages reasoning controls per model family. Current models take a named effort with
/// adaptive thinking; older models take a thinking-token budget. A model this table does not recognise
/// offers no levels.
/// </summary>
internal static class AnthropicReasoning
{
    private enum Family
    {
        Unknown,
        AdaptiveAlwaysOn,
        AdaptiveDisableable,
        Adaptive46,
        EffortOnly,
        BudgetOnly
    }

    private static readonly IReadOnlySet<LlmReasoningLevel> Effort = LlmReasoningEffort.Levels(
        LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High,
        LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum);

    private static readonly IReadOnlySet<LlmReasoningLevel> EffortWithNone = LlmReasoningEffort.Levels(
        LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High,
        LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum);

    private static readonly IReadOnlySet<LlmReasoningLevel> Effort46 = LlmReasoningEffort.Levels(
        LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High,
        LlmReasoningLevel.Maximum);

    private static readonly IReadOnlySet<LlmReasoningLevel> Effort45 = LlmReasoningEffort.Levels(
        LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High);

    /// <summary>Thinking-token budgets for models without effort. The API minimum is 1,024.</summary>
    private static readonly FrozenDictionary<LlmReasoningLevel, int> Budgets = new Dictionary<LlmReasoningLevel, int>
    {
        [LlmReasoningLevel.Low] = 1_024,
        [LlmReasoningLevel.Medium] = 4_096,
        [LlmReasoningLevel.High] = 16_384,
        [LlmReasoningLevel.ExtraHigh] = 32_768,
        [LlmReasoningLevel.Maximum] = 64_000
    }.ToFrozenDictionary();

    // Ordered so that a longer, more specific prefix is tested before a shorter one it contains.
    private static readonly (string Prefix, Family Family)[] Prefixes =
    [
        ("claude-fable-", Family.AdaptiveAlwaysOn),
        ("claude-mythos-", Family.AdaptiveAlwaysOn),
        ("claude-opus-5-5", Family.AdaptiveAlwaysOn),
        ("claude-opus-5", Family.AdaptiveDisableable),
        ("claude-sonnet-5", Family.AdaptiveDisableable),
        ("claude-opus-4-8", Family.AdaptiveDisableable),
        ("claude-opus-4-7", Family.AdaptiveDisableable),
        ("claude-opus-4-6", Family.Adaptive46),
        ("claude-sonnet-4-6", Family.Adaptive46),
        ("claude-opus-4-5", Family.EffortOnly),
        ("claude-haiku-4-5", Family.BudgetOnly),
        ("claude-sonnet-4-5", Family.BudgetOnly),
        ("claude-opus-4-1", Family.BudgetOnly),
        ("claude-opus-4", Family.BudgetOnly),
        ("claude-sonnet-4", Family.BudgetOnly),
        ("claude-3-7-sonnet", Family.BudgetOnly)
    ];

    public static IReadOnlySet<LlmReasoningLevel> Levels(string? modelId) => Classify(modelId) switch
    {
        Family.AdaptiveAlwaysOn => Effort,
        Family.AdaptiveDisableable => EffortWithNone,
        Family.Adaptive46 => Effort46,
        Family.EffortOnly => Effort45,
        Family.BudgetOnly => LlmReasoningEffort.Levels(
            LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High,
            LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum),
        _ => FrozenSet<LlmReasoningLevel>.Empty
    };

    /// <summary>
    /// Writes the level into the payload. Validation has already confirmed the model offers it.
    /// <paramref name="maxOutputTokens"/> is the request's output allowance; a thinking budget is added
    /// to it so neither is reduced.
    /// </summary>
    public static void Apply(JsonObject payload, string modelId, LlmReasoningLevel level, int maxOutputTokens)
    {
        switch (Classify(modelId))
        {
            case Family.AdaptiveAlwaysOn or Family.AdaptiveDisableable or Family.Adaptive46 when level == LlmReasoningLevel.None:
                // Opus 4.6 and Sonnet 4.6 run without thinking when the field is absent; later models
                // that accept disabled thinking need it stated.
                if (Classify(modelId) == Family.AdaptiveDisableable)
                {
                    payload["thinking"] = new JsonObject { ["type"] = "disabled" };
                }

                break;
            case Family.AdaptiveAlwaysOn or Family.AdaptiveDisableable or Family.Adaptive46:
                payload["thinking"] = new JsonObject { ["type"] = "adaptive" };
                payload["output_config"] = new JsonObject { ["effort"] = LlmReasoningEffort.Word(level) };
                break;
            case Family.EffortOnly when level != LlmReasoningLevel.None:
                payload["output_config"] = new JsonObject { ["effort"] = LlmReasoningEffort.Word(level) };
                break;
            case Family.BudgetOnly when level != LlmReasoningLevel.None:
                int budget = Budgets[level];
                payload["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
                payload["max_tokens"] = checked(maxOutputTokens + budget);
                break;
        }
    }

    private static Family Classify(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return Family.Unknown;
        }

        foreach ((string prefix, Family family) in Prefixes)
        {
            if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return family;
            }
        }

        return Family.Unknown;
    }
}
