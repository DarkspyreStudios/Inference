using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>How a Claude model takes reasoning: adaptive thinking with an effort, an effort alone, or a token budget.</summary>
internal enum AnthropicThinkingMode
{
    None,
    Adaptive,
    EffortOnly,
    Budget
}

/// <summary>How <see cref="LlmReasoningLevel.None"/> reaches a Claude model, or that the model has no such setting.</summary>
internal enum AnthropicNoneMapping
{
    Unsupported,
    OmitThinking,
    Disabled
}

/// <summary>
/// The reasoning and request rules of one Claude model, shared by Anthropic Messages, Vertex Claude and Bedrock
/// Claude. <see cref="SupportsStructuredOutputs"/> is null when no catalog entry reports it.
/// </summary>
internal sealed record AnthropicModelProfile(
    AnthropicThinkingMode Thinking,
    AnthropicNoneMapping NoneMapping,
    IReadOnlySet<LlmReasoningLevel> ReasoningLevels,
    bool AllowsSampling,
    bool AllowsForcedToolChoice,
    bool? SupportsStructuredOutputs,
    long? MaxInputTokens,
    long? MaxOutputTokens)
{
    public bool ThinksByDefault { get; init; }
    public bool DisplayDefaultsToOmitted { get; init; }
}

/// <summary>The request fields a reasoning level adds: <c>thinking</c>, <c>output_config</c> and a raised <c>max_tokens</c>.</summary>
internal sealed record AnthropicReasoningFields(JsonObject? Thinking, JsonObject? OutputConfig, int? MaxTokens);

/// <summary>
/// Claude model profiles. The first-party Models API reports each model's thinking types and effort levels; the
/// family table holds what it does not report, and every profile when there is no catalog entry.
/// </summary>
internal static class AnthropicModelProfiles
{
    internal static void ValidateSampling(LlmChatRequest request, AnthropicModelProfile profile)
    {
        if (!profile.AllowsSampling && (request.Temperature is not null || request.TopP is not null || request.TopK is not null))
        { throw new NotSupportedException("This Claude model does not accept temperature, top-p or top-k."); }
        if (request.Temperature is < 0 or > 1)
        { throw new ArgumentOutOfRangeException(nameof(request), "Claude temperature must be between 0 and 1."); }
        bool thinking = request.ReasoningLevel is > LlmReasoningLevel.None
            || request.ReasoningLevel is null && profile.ThinksByDefault;
        if (thinking && (request.TopK is not null || request.TopP is < 0.95))
        { throw new NotSupportedException("With thinking, Claude accepts top_p from 0.95 to 1 and no top_k."); }
    }

    private sealed record FamilyRow(
        AnthropicThinkingMode Thinking,
        AnthropicNoneMapping NoneMapping,
        bool AllowsSampling,
        bool AllowsForcedToolChoice,
        IReadOnlySet<LlmReasoningLevel> Levels)
    {
        public bool ThinksByDefault { get; init; }
        public bool DisplayDefaultsToOmitted { get; init; }
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

    /// <summary>Thinking-token budgets for budget models. The API minimum is 1,024.</summary>
    private static readonly FrozenDictionary<LlmReasoningLevel, int> Budgets = new Dictionary<LlmReasoningLevel, int>
    {
        [LlmReasoningLevel.Low] = 1_024,
        [LlmReasoningLevel.Medium] = 4_096,
        [LlmReasoningLevel.High] = 16_384,
        [LlmReasoningLevel.ExtraHigh] = 32_768,
        [LlmReasoningLevel.Maximum] = 64_000
    }.ToFrozenDictionary();

    private static readonly FamilyRow AlwaysOnNoForcedTools = new(
        AnthropicThinkingMode.Adaptive, AnthropicNoneMapping.Unsupported, false, false, Effort)
    { ThinksByDefault = true, DisplayDefaultsToOmitted = true };

    private static readonly FamilyRow AlwaysOn = new(
        AnthropicThinkingMode.Adaptive, AnthropicNoneMapping.Unsupported, false, true, Effort)
    { ThinksByDefault = true, DisplayDefaultsToOmitted = true };

    private static readonly FamilyRow Disableable = new(
        AnthropicThinkingMode.Adaptive, AnthropicNoneMapping.Disabled, false, true, EffortWithNone)
    { ThinksByDefault = true, DisplayDefaultsToOmitted = true };

    private static readonly FamilyRow AdaptiveOmitted = Disableable with { ThinksByDefault = false };

    private static readonly FamilyRow Adaptive46 = new(
        AnthropicThinkingMode.Adaptive, AnthropicNoneMapping.OmitThinking, true, true, Effort46);

    private static readonly FamilyRow EffortOnly = new(
        AnthropicThinkingMode.EffortOnly, AnthropicNoneMapping.OmitThinking, true, true, Effort45);

    private static readonly FamilyRow Budget = new(
        AnthropicThinkingMode.Budget, AnthropicNoneMapping.OmitThinking, true, true, EffortWithNone);

    // Ordered so that a longer, more specific prefix is tested before a shorter one it contains.
    private static readonly (string Prefix, FamilyRow Row)[] Families =
    [
        ("claude-fable-5-1", AlwaysOnNoForcedTools),
        ("claude-mythos-5-1", AlwaysOnNoForcedTools),
        ("claude-fable-", AlwaysOn),
        ("claude-mythos-", AlwaysOn),
        ("claude-opus-5-5", AlwaysOnNoForcedTools),
        // Sonnet 5.5 rejects disabled thinking at every effort; between_tools is its lowest setting, not None.
        ("claude-sonnet-5-5", AlwaysOnNoForcedTools),
        ("claude-opus-5", Disableable),
        ("claude-sonnet-5", Disableable),
        ("claude-opus-4-8", AdaptiveOmitted),
        ("claude-opus-4-7", AdaptiveOmitted),
        ("claude-opus-4-6", Adaptive46),
        ("claude-sonnet-4-6", Adaptive46),
        ("claude-opus-4-5", EffortOnly),
        ("claude-haiku-4-5", Budget),
        ("claude-sonnet-4-5", Budget),
        ("claude-opus-4-1", Budget),
        ("claude-opus-4", Budget),
        ("claude-sonnet-4", Budget),
        ("claude-3-7-sonnet", Budget)
    ];

    /// <summary>A model with neither a catalog entry nor a family row offers no levels.</summary>
    private static readonly AnthropicModelProfile Unknown = new(
        AnthropicThinkingMode.None, AnthropicNoneMapping.Unsupported, LlmReasoningEffort.Levels(),
        AllowsSampling: false, AllowsForcedToolChoice: true, SupportsStructuredOutputs: null,
        MaxInputTokens: null, MaxOutputTokens: null);

    /// <summary>
    /// The profile of a bare Anthropic model id. A first-party Models API entry supplies the thinking mode, levels,
    /// structured-output support and limits; the family row supplies the None mapping and the request rules.
    /// </summary>
    public static AnthropicModelProfile Resolve(string? modelId, JsonElement? catalogEntry)
    {
        FamilyRow? row = Family(modelId);
        if (catalogEntry is { ValueKind: JsonValueKind.Object } entry)
        {
            (AnthropicThinkingMode thinking, HashSet<LlmReasoningLevel> levels) = CatalogReasoning(entry);
            AnthropicNoneMapping none = row?.NoneMapping ?? AnthropicNoneMapping.Unsupported;
            if (none != AnthropicNoneMapping.Unsupported && levels.Count > 0)
            {
                levels.Add(LlmReasoningLevel.None);
            }
            return new AnthropicModelProfile(
                thinking,
                none,
                levels.ToFrozenSet(),
                row?.AllowsSampling ?? false,
                row?.AllowsForcedToolChoice ?? true,
                Supported(entry, "capabilities", "structured_outputs"),
                Long(entry, "max_input_tokens"),
                Long(entry, "max_tokens"))
            {
                ThinksByDefault = thinking == AnthropicThinkingMode.Adaptive && row?.ThinksByDefault == true,
                DisplayDefaultsToOmitted = row?.DisplayDefaultsToOmitted == true
            };
        }

        return row is null
            ? Unknown
            : new AnthropicModelProfile(row.Thinking, row.NoneMapping, row.Levels, row.AllowsSampling,
                row.AllowsForcedToolChoice, SupportsStructuredOutputs: null, MaxInputTokens: null, MaxOutputTokens: null)
            { ThinksByDefault = row.ThinksByDefault, DisplayDefaultsToOmitted = row.DisplayDefaultsToOmitted };
    }

    /// <summary>
    /// The fields a level or summary request adds. Validation has already confirmed the profile offers the level.
    /// <paramref name="maxOutputTokens"/> is the request's output allowance; a thinking budget is added to it so
    /// neither is reduced.
    /// </summary>
    public static AnthropicReasoningFields Fields(AnthropicModelProfile profile, LlmReasoningLevel? level,
        int maxOutputTokens, bool requestReasoningText)
    {
        if (level is null)
        {
            return profile.ThinksByDefault && requestReasoningText
                ? new(AdaptiveThinking(profile, requestReasoningText), null, null)
                : new(null, null, null);
        }
        if (level == LlmReasoningLevel.None)
        {
            // Models that run without thinking when the field is absent need nothing; models that think by default
            // and accept disabled thinking need it stated.
            return profile.NoneMapping == AnthropicNoneMapping.Disabled
                ? new(new JsonObject { ["type"] = "disabled" }, null, null)
                : new(null, null, null);
        }

        return profile.Thinking switch
        {
            AnthropicThinkingMode.Adaptive => new(
                AdaptiveThinking(profile, requestReasoningText),
                new JsonObject { ["effort"] = LlmReasoningEffort.Word(level.Value) },
                null),
            AnthropicThinkingMode.EffortOnly => new(
                null,
                new JsonObject { ["effort"] = LlmReasoningEffort.Word(level.Value) },
                null),
            AnthropicThinkingMode.Budget => new(
                new JsonObject { ["type"] = "enabled", ["budget_tokens"] = Budgets[level.Value] },
                null,
                checked(maxOutputTokens + Budgets[level.Value])),
            _ => new(null, null, null)
        };
    }

    private static JsonObject AdaptiveThinking(AnthropicModelProfile profile, bool requestReasoningText)
    {
        var thinking = new JsonObject { ["type"] = "adaptive" };
        if (requestReasoningText && profile.DisplayDefaultsToOmitted) { thinking["display"] = "summarized"; }
        return thinking;
    }

    private static FamilyRow? Family(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        foreach ((string prefix, FamilyRow row) in Families)
        {
            if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>
    /// Adaptive thinking takes the supported effort levels; budget thinking alone takes Low to Maximum; an effort
    /// without thinking takes the supported effort levels.
    /// </summary>
    private static (AnthropicThinkingMode Thinking, HashSet<LlmReasoningLevel> Levels) CatalogReasoning(JsonElement entry)
    {
        if (Supported(entry, "capabilities", "thinking", "types", "adaptive") == true)
        {
            return (AnthropicThinkingMode.Adaptive, EffortLevels(entry));
        }
        if (Supported(entry, "capabilities", "thinking", "types", "enabled") == true)
        {
            return (AnthropicThinkingMode.Budget, [.. Budgets.Keys]);
        }
        return Supported(entry, "capabilities", "effort") == true
            ? (AnthropicThinkingMode.EffortOnly, EffortLevels(entry))
            : (AnthropicThinkingMode.None, []);
    }

    private static HashSet<LlmReasoningLevel> EffortLevels(JsonElement entry)
    {
        var levels = new HashSet<LlmReasoningLevel>();
        foreach ((string word, LlmReasoningLevel level) in new[]
                 {
                     ("low", LlmReasoningLevel.Low), ("medium", LlmReasoningLevel.Medium), ("high", LlmReasoningLevel.High),
                     ("xhigh", LlmReasoningLevel.ExtraHigh), ("max", LlmReasoningLevel.Maximum)
                 })
        {
            if (Supported(entry, "capabilities", "effort", word) == true)
            {
                levels.Add(level);
            }
        }
        return levels;
    }

    /// <summary>The <c>supported</c> flag at the path, or null when any step is missing or null.</summary>
    private static bool? Supported(JsonElement entry, params string[] path)
    {
        JsonElement current = entry;
        foreach (string step in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(step, out current))
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.Object
            && current.TryGetProperty("supported", out JsonElement supported)
            && supported.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? supported.GetBoolean()
                : null;
    }

    private static long? Long(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long number)
                ? number
                : null;
}
