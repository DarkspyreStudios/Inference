using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed record XaiMultiAgentProfile(IReadOnlyDictionary<string, int> AgentCounts);

internal sealed record XaiModelProfile(
    IReadOnlySet<LlmReasoningLevel> ReasoningLevels,
    LlmReasoningLevel? DefaultReasoningLevel,
    bool AllowsSampling,
    bool AcceptsImages = false,
    XaiMultiAgentProfile? MultiAgent = null);

/// <summary>Catalog facts take precedence over documented model-family fallbacks.</summary>
internal static class XaiModelProfiles
{
    private static readonly XaiModelProfile Unknown = new(LlmReasoningEffort.Levels(), null, false);
    private static readonly XaiModelProfile Reasoning = new(LlmReasoningEffort.Levels(
        LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh), LlmReasoningLevel.High, true);

    // https://docs.x.ai/developers/model-capabilities/text/reasoning
    // https://docs.x.ai/developers/model-capabilities/text/multi-agent
    private static readonly (string Prefix, XaiModelProfile Profile)[] Rows =
    [
        ("grok-4.20-non-reasoning", new(LlmReasoningEffort.Levels(), null, true)),
        ("grok-4.20-multi-agent", new(LlmReasoningEffort.Levels(), null, true, MultiAgent: new(
            new Dictionary<string, int>(StringComparer.Ordinal) { ["low"] = 4, ["medium"] = 4, ["high"] = 16, ["xhigh"] = 16 }.ToFrozenDictionary(StringComparer.Ordinal)))),
        ("grok-4.7", Reasoning),
        ("grok-4.6", Reasoning),
        ("grok-4.5", new(LlmReasoningEffort.Levels(LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High), LlmReasoningLevel.High, true))
    ];

    internal static XaiModelProfile Get(LlmConnection connection, string? modelId)
    {
        LlmModel? model = modelId is null ? null : connection.Catalog?.FirstOrDefault(candidate => candidate.Id == modelId)
            ?? connection.Catalog?.FirstOrDefault(candidate => candidate.CatalogEntry is { } entry
                && Contains(entry, "aliases", modelId));
        string? canonicalId = model?.Id ?? modelId;
        XaiModelProfile profile = Rows.FirstOrDefault(row => canonicalId?.StartsWith(row.Prefix, StringComparison.OrdinalIgnoreCase) == true).Profile ?? Unknown;
        if (model?.CatalogEntry is not { } catalog) return profile;
        profile = profile with { AcceptsImages = Contains(catalog, "input_modalities", "image") };
        if (profile.MultiAgent is not null) return profile;
        if (catalog.TryGetProperty("capabilities", out JsonElement capabilities) && capabilities.ValueKind == JsonValueKind.Object
            && capabilities.TryGetProperty("reasoning_effort", out JsonElement efforts) && efforts.ValueKind == JsonValueKind.Array)
        {
            var levels = efforts.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => Level(item.GetString())).OfType<LlmReasoningLevel>().ToFrozenSet();
            LlmReasoningLevel? defaultLevel = capabilities.TryGetProperty("default_reasoning_effort", out JsonElement value)
                && value.ValueKind == JsonValueKind.String ? Level(value.GetString()) : null;
            profile = profile with { ReasoningLevels = levels, DefaultReasoningLevel = defaultLevel is { } level && levels.Contains(level) ? level : null };
        }
        return profile;
    }

    private static LlmReasoningLevel? Level(string? word) => word switch
    {
        "low" => LlmReasoningLevel.Low,
        "medium" => LlmReasoningLevel.Medium,
        "high" => LlmReasoningLevel.High,
        "xhigh" => LlmReasoningLevel.ExtraHigh,
        _ => null
    };

    private static bool Contains(JsonElement entry, string property, string value) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(property, out JsonElement values)
        && values.ValueKind == JsonValueKind.Array && values.EnumerateArray()
            .Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == value);
}
