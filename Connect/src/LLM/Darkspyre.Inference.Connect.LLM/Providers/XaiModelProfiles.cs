using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal sealed record XaiMultiAgentProfile(IReadOnlyDictionary<string, int> AgentCounts);

internal sealed record XaiModelProfile(
    IReadOnlySet<LlmReasoningLevel> ReasoningLevels,
    LlmReasoningLevel? DefaultReasoningLevel,
    InferenceSupport AllowsSampling,
    InferenceSupport AcceptsImages = InferenceSupport.Unknown,
    XaiMultiAgentProfile? MultiAgent = null);

/// <summary>Catalog facts take precedence over documented model-family fallbacks.</summary>
internal static class XaiModelProfiles
{
    private static readonly XaiModelProfile Unknown = new(LlmReasoningEffort.Levels(), null, InferenceSupport.Unknown);
    private static readonly XaiModelProfile NonReasoning = new(LlmReasoningEffort.Levels(), null, InferenceSupport.Supported);
    private static readonly XaiModelProfile Reasoning = new(LlmReasoningEffort.Levels(
        LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh), LlmReasoningLevel.High, InferenceSupport.Supported);

    // https://docs.x.ai/developers/model-capabilities/text/reasoning
    // https://docs.x.ai/developers/model-capabilities/text/multi-agent
    // https://docs.x.ai/developers/models/grok-4.20-0309-non-reasoning
    private static readonly (string Prefix, XaiModelProfile Profile)[] Rows =
    [
        ("grok-4.20-0309-non-reasoning", NonReasoning),
        ("grok-4.20-non-reasoning", NonReasoning),
        ("grok-4.20-multi-agent", new(LlmReasoningEffort.Levels(), null, InferenceSupport.Supported, MultiAgent: new(
            new Dictionary<string, int>(StringComparer.Ordinal) { ["low"] = 4, ["medium"] = 4, ["high"] = 16, ["xhigh"] = 16 }.ToFrozenDictionary(StringComparer.Ordinal)))),
        ("grok-4.7", Reasoning),
        ("grok-4.6", Reasoning),
        ("grok-4.5", new(LlmReasoningEffort.Levels(LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High), LlmReasoningLevel.High, InferenceSupport.Supported))
    ];

    internal static XaiModelProfile Get(LlmConnection connection, string? modelId)
    {
        LlmModel? model = XaiCatalog.FindModel(connection.Catalog, modelId);
        string? canonicalId = model?.Id ?? modelId;
        XaiModelProfile profile = Rows.FirstOrDefault(row => canonicalId?.StartsWith(row.Prefix, StringComparison.OrdinalIgnoreCase) == true).Profile ?? Unknown;
        if (model?.CatalogEntry is not { } catalog) return profile;
        profile = profile with { AcceptsImages = Modality(catalog, "image") };
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

    internal static string MultiAgentEffort(XaiMultiAgentProfile profile, int count)
    {
        if (profile.AgentCounts.TryGetValue("low", out int low) && count == low) return "low";
        if (profile.AgentCounts.TryGetValue("high", out int high) && count == high) return "high";
        throw new NotSupportedException("The selected multi-agent profile does not support the requested agent count.");
    }

    private static LlmReasoningLevel? Level(string? word) => word switch
    {
        "low" => LlmReasoningLevel.Low,
        "medium" => LlmReasoningLevel.Medium,
        "high" => LlmReasoningLevel.High,
        "xhigh" => LlmReasoningLevel.ExtraHigh,
        _ => null
    };

    private static InferenceSupport Modality(JsonElement entry, string value) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("input_modalities", out JsonElement values)
        && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == value)
                ? InferenceSupport.Supported : InferenceSupport.Unsupported
            : InferenceSupport.Unknown;
}
