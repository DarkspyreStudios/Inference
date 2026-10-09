using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>How a Gemini model takes reasoning controls, or whether that information is unknown.</summary>
internal enum GeminiThinkingControl
{
    Unknown,
    None,
    Level,
    Budget
}

/// <summary>A Gemini model's controls. <see cref="Budgets"/> applies to budget control only.</summary>
internal sealed record GeminiModelProfile(
    GeminiThinkingControl Control,
    IReadOnlySet<LlmReasoningLevel> Levels,
    IReadOnlyDictionary<LlmReasoningLevel, int> Budgets)
{
    public bool AllowsSampling { get; init; } = true;
    public bool MultimodalFunctionResponses { get; init; }
    public bool RequiresThoughtSignatures { get; init; }
    public bool StreamedFunctionArguments { get; init; }
}

/// <summary>Per-model Gemini reasoning controls, shared by the Gemini API and Vertex Gemini routes.</summary>
internal static class GeminiModelProfiles
{
    private static readonly FrozenSet<LlmReasoningLevel> MinimalToHigh = new[]
    { LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High }.ToFrozenSet();

    private static readonly IReadOnlySet<LlmReasoningLevel> LowToHigh = new[]
    { LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High }.ToFrozenSet();

    private static readonly FrozenDictionary<LlmReasoningLevel, int> NoBudgets = FrozenDictionary<LlmReasoningLevel, int>.Empty;

    private static readonly GeminiModelProfile NoControl = new(
        GeminiThinkingControl.None, new[] { LlmReasoningLevel.None }.ToFrozenSet(), NoBudgets);

    private static readonly GeminiModelProfile UnknownControl = new(
        GeminiThinkingControl.Unknown, FrozenSet<LlmReasoningLevel>.Empty, NoBudgets);

    // Gemini 3 thinkingLevel sets. Longest prefix first, so a longer, more specific prefix is tested before a shorter
    // one it contains.
    private static readonly (string Prefix, GeminiModelProfile Profile)[] Rows =
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
    public static GeminiModelProfile Get(string bareModelId, bool? catalogThinking)
    {
        GeminiModelProfile resolved = UnknownControl;
        foreach ((string prefix, GeminiModelProfile profile) in Rows)
        {
            if (bareModelId.StartsWith(prefix, StringComparison.Ordinal))
            {
                resolved = profile;
                break;
            }
        }
        if (catalogThinking == false) { resolved = NoControl; }
        // Flash 3.6 and later ignore temperature, top-p and top-k, independently of thinking support.
        string[] segments = bareModelId.Split('-');
        bool ignoredSampling = segments is ["gemini", var versionText, "flash", ..]
            && Version.TryParse(versionText.Contains('.') ? versionText : versionText + ".0", out Version? version)
            && version >= new Version(3, 6);
        bool gemini3 = bareModelId.StartsWith("gemini-3", StringComparison.Ordinal);
        return resolved with
        {
            AllowsSampling = !ignoredSampling,
            MultimodalFunctionResponses = gemini3,
            RequiresThoughtSignatures = gemini3,
            StreamedFunctionArguments = gemini3 && !bareModelId.StartsWith("gemini-3.1-flash-lite", StringComparison.Ordinal)
        };
    }

    /// <summary>Maps desired effort through Gemini-owned native-choice rows without changing caller intent.</summary>
    public static LlmReasoningLevel? MapReasoning(GeminiModelProfile profile, LlmReasoningLevel? requested)
    {
        if (requested is null) return null;
        if (profile.Control == GeminiThinkingControl.None) return LlmReasoningLevel.None;
        LlmReasoningLevel[] row = requested switch
        {
            LlmReasoningLevel.None => [LlmReasoningLevel.None, LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High],
            LlmReasoningLevel.Minimal => [LlmReasoningLevel.Minimal, LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High],
            LlmReasoningLevel.Low => [LlmReasoningLevel.Low, LlmReasoningLevel.Minimal, LlmReasoningLevel.None, LlmReasoningLevel.Medium, LlmReasoningLevel.High],
            LlmReasoningLevel.Medium => [LlmReasoningLevel.Medium, LlmReasoningLevel.Low, LlmReasoningLevel.Minimal, LlmReasoningLevel.None, LlmReasoningLevel.High],
            LlmReasoningLevel.High or LlmReasoningLevel.ExtraHigh or LlmReasoningLevel.Maximum =>
                [LlmReasoningLevel.High, LlmReasoningLevel.Medium, LlmReasoningLevel.Low, LlmReasoningLevel.Minimal, LlmReasoningLevel.None],
            _ => throw new ArgumentOutOfRangeException(nameof(requested))
        };
        IReadOnlySet<LlmReasoningLevel> choices = profile.Levels.Count == 0
            ? profile.Control == GeminiThinkingControl.Budget ? profile.Budgets.Keys.ToFrozenSet() : MinimalToHigh
            : profile.Levels;
        foreach (LlmReasoningLevel choice in row)
        {
            bool native = profile.Control == GeminiThinkingControl.Budget
                ? profile.Budgets.ContainsKey(choice) : MinimalToHigh.Contains(choice);
            if (native && choices.Contains(choice)) return choice;
        }
        throw new NotSupportedException("The selected Gemini native choices contain no mapped thinking level or budget.");
    }

    /// <summary>
    /// The <c>thinkingConfig</c> for mapped desired effort. It never writes both <c>thinkingLevel</c> and
    /// <c>thinkingBudget</c>, which Gemini rejects.
    /// </summary>
    public static JsonObject? ThinkingConfig(GeminiModelProfile profile, LlmReasoningLevel? level, bool requestReasoningText)
    {
        level = MapReasoning(profile, level);
        JsonObject? config = level is null ? null : profile.Control switch
        {
            GeminiThinkingControl.Level or GeminiThinkingControl.Unknown => new JsonObject { ["thinkingLevel"] = level.Value.ToString().ToUpperInvariant() },
            GeminiThinkingControl.Budget when level == LlmReasoningLevel.None => new JsonObject { ["thinkingBudget"] = 0 },
            GeminiThinkingControl.Budget => new JsonObject { ["thinkingBudget"] = profile.Budgets[level.Value] },
            GeminiThinkingControl.None => null,
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
        if (requestReasoningText && level != LlmReasoningLevel.None && profile.Control != GeminiThinkingControl.None)
        {
            config ??= new JsonObject();
            config["includeThoughts"] = true;
        }
        return config;
    }

    private static GeminiModelProfile Level(IReadOnlySet<LlmReasoningLevel> levels) =>
        new(GeminiThinkingControl.Level, levels, NoBudgets);

    /// <summary>Low is 1,024, which satisfies the Flash-Lite minimum of 512, the Pro minimum of 128 and Vertex's 1.</summary>
    private static GeminiModelProfile Budget(bool disables, int high)
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
