using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>How a Gemini model takes a reasoning level: a named thinking level, a thinking-token budget, or neither.</summary>
internal enum GeminiThinkingControl
{
    None,
    Level,
    Budget
}

/// <summary>A Gemini model's reasoning control. <see cref="Budgets"/> applies to budget control only.</summary>
internal sealed record GeminiReasoningProfile(
    GeminiThinkingControl Control,
    IReadOnlySet<LlmReasoningLevel> Levels,
    IReadOnlyDictionary<LlmReasoningLevel, int> Budgets);

/// <summary>Per-model Gemini reasoning controls, shared by the Gemini API and Vertex Gemini routes.</summary>
internal static class GeminiReasoning
{
    private static readonly IReadOnlySet<LlmReasoningLevel> MinimalToHigh = new[]
    { LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High }.ToFrozenSet();

    private static readonly IReadOnlySet<LlmReasoningLevel> LowToHigh = new[]
    { LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High }.ToFrozenSet();

    private static readonly FrozenDictionary<LlmReasoningLevel, int> NoBudgets = FrozenDictionary<LlmReasoningLevel, int>.Empty;

    private static readonly GeminiReasoningProfile NoControl = new(
        GeminiThinkingControl.None, FrozenSet<LlmReasoningLevel>.Empty, NoBudgets);

    // Gemini 3 thinkingLevel sets. Longest prefix first, so a longer, more specific prefix is tested before a shorter
    // one it contains.
    private static readonly (string Prefix, GeminiReasoningProfile Profile)[] Rows =
    [
        ("gemini-3.8-flash", Level(LowToHigh)),
        ("gemini-3.7-flash", Level(LowToHigh)),
        ("gemini-3.6-flash", Level(MinimalToHigh)),
        ("gemini-3.5-flash-lite", Level(MinimalToHigh)),
        ("gemini-3.5-flash", Level(MinimalToHigh)),
        ("gemini-3.1-flash-lite", Level(MinimalToHigh)),
        ("gemini-3.1-pro", Level(LowToHigh)),
        ("gemini-3-flash", Level(MinimalToHigh)),
        ("gemini-3-pro", Level(new[] { LlmReasoningLevel.Low, LlmReasoningLevel.High }.ToFrozenSet())),
        // Gemini 2.5 takes thinkingBudget. Flash and Flash-Lite disable thinking at 0; Pro cannot disable it.
        ("gemini-2.5-flash-lite", Budget(disables: true, high: 24_576)),
        ("gemini-2.5-flash", Budget(disables: true, high: 24_576)),
        ("gemini-2.5-pro", Budget(disables: false, high: 32_768))
    ];

    /// <summary>
    /// The profile of a bare Gemini model id. <paramref name="catalogThinking"/> is the Gemini API catalog's
    /// <c>thinking</c> flag; false means the model takes no thinking configuration at all. Null uses the table.
    /// </summary>
    public static GeminiReasoningProfile Get(string bareModelId, bool? catalogThinking)
    {
        if (catalogThinking == false)
        {
            return NoControl;
        }

        foreach ((string prefix, GeminiReasoningProfile profile) in Rows)
        {
            if (bareModelId.StartsWith(prefix, StringComparison.Ordinal))
            {
                return profile;
            }
        }

        return NoControl;
    }

    /// <summary>
    /// The <c>thinkingConfig</c> for a level the profile offers. It never writes both <c>thinkingLevel</c> and
    /// <c>thinkingBudget</c>, which Gemini rejects.
    /// </summary>
    public static JsonObject ThinkingConfig(GeminiReasoningProfile profile, LlmReasoningLevel level) => profile.Control switch
    {
        GeminiThinkingControl.Level => new JsonObject
        {
            ["thinkingLevel"] = level.ToString().ToUpperInvariant(),
            ["includeThoughts"] = true
        },
        GeminiThinkingControl.Budget when level == LlmReasoningLevel.None => new JsonObject { ["thinkingBudget"] = 0 },
        GeminiThinkingControl.Budget => new JsonObject
        {
            ["thinkingBudget"] = profile.Budgets[level],
            ["includeThoughts"] = true
        },
        _ => throw new ArgumentOutOfRangeException(nameof(profile), "The model takes no reasoning level.")
    };

    private static GeminiReasoningProfile Level(IReadOnlySet<LlmReasoningLevel> levels) =>
        new(GeminiThinkingControl.Level, levels, NoBudgets);

    /// <summary>Low is 1,024, which satisfies the Flash-Lite minimum of 512, the Pro minimum of 128 and Vertex's 1.</summary>
    private static GeminiReasoningProfile Budget(bool disables, int high)
    {
        var budgets = new Dictionary<LlmReasoningLevel, int>
        {
            [LlmReasoningLevel.Low] = 1_024,
            [LlmReasoningLevel.Medium] = 8_192,
            [LlmReasoningLevel.High] = high
        };
        if (disables)
        {
            budgets[LlmReasoningLevel.None] = 0;
        }
        return new(GeminiThinkingControl.Budget, budgets.Keys.ToFrozenSet(), budgets.ToFrozenDictionary());
    }
}
