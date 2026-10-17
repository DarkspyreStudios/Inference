using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>A multi-agent model's two agent counts: Low runs under reasoning effort low, High under high.</summary>
internal sealed record XaiMultiAgentProfile(int Low, int High)
{
    /// <summary>The counts the model offers, in ascending order.</summary>
    internal IReadOnlyList<int> AgentCounts => [Low, High];

    /// <summary>The reasoning effort that runs the offered count nearest <paramref name="count"/>; between two equally
    /// near counts, the larger, as the request normalizer chooses.</summary>
    internal string Effort(int count) => Math.Abs((long)count - High) <= Math.Abs((long)count - Low) ? "high" : "low";
}

internal sealed record XaiModelProfile(
    IReadOnlySet<LlmReasoningLevel> ReasoningLevels,
    LlmReasoningLevel? DefaultReasoningLevel,
    InferenceSupport AllowsSampling,
    InferenceSupport AcceptsImages = InferenceSupport.Unknown,
    XaiMultiAgentProfile? MultiAgent = null);

/// <summary>Grok model facts: catalog facts take precedence over documented model-family fallbacks.</summary>
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
        ("grok-4.20-multi-agent", new(LlmReasoningEffort.Levels(), null, InferenceSupport.Supported, MultiAgent: new(Low: 4, High: 16))),
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

    internal static LlmReasoningLevel? MapReasoning(XaiModelProfile profile,
        IReadOnlySet<LlmReasoningLevel> offered, LlmReasoningLevel? requested)
    {
        // A multi-agent model's effort is its agent count, so a requested level is dropped (B8, B15).
        if (requested is null || profile.MultiAgent is not null) return null;
        // Each row names Grok's downward mappings, then its below-minimum choices in ascending order.
        LlmReasoningLevel[] row = requested.Value switch
        {
            LlmReasoningLevel.None => [LlmReasoningLevel.None, LlmReasoningLevel.Minimal, LlmReasoningLevel.Low,
                LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.Minimal => [LlmReasoningLevel.Minimal, LlmReasoningLevel.None, LlmReasoningLevel.Low,
                LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.Low => [LlmReasoningLevel.Low, LlmReasoningLevel.Minimal, LlmReasoningLevel.None,
                LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.Medium => [LlmReasoningLevel.Medium, LlmReasoningLevel.Low, LlmReasoningLevel.Minimal,
                LlmReasoningLevel.None, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.High => [LlmReasoningLevel.High, LlmReasoningLevel.Medium, LlmReasoningLevel.Low,
                LlmReasoningLevel.Minimal, LlmReasoningLevel.None, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.ExtraHigh => [LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.High, LlmReasoningLevel.Medium,
                LlmReasoningLevel.Low, LlmReasoningLevel.Minimal, LlmReasoningLevel.None, LlmReasoningLevel.Maximum],
            LlmReasoningLevel.Maximum => [LlmReasoningLevel.Maximum, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.High,
                LlmReasoningLevel.Medium, LlmReasoningLevel.Low, LlmReasoningLevel.Minimal, LlmReasoningLevel.None],
            _ => throw new ArgumentOutOfRangeException(nameof(requested), requested, "Unknown reasoning level.")
        };
        return offered.Count == 0 ? requested : row.First(offered.Contains);
    }

    private static LlmReasoningLevel? Level(string? word) => word switch
    {
        "none" => LlmReasoningLevel.None,
        "minimal" => LlmReasoningLevel.Minimal,
        "low" => LlmReasoningLevel.Low,
        "medium" => LlmReasoningLevel.Medium,
        "high" => LlmReasoningLevel.High,
        "xhigh" => LlmReasoningLevel.ExtraHigh,
        "max" => LlmReasoningLevel.Maximum,
        _ => null
    };

    private static InferenceSupport Modality(JsonElement entry, string value) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("input_modalities", out JsonElement values)
        && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == value)
                ? InferenceSupport.Supported : InferenceSupport.Unsupported
            : InferenceSupport.Unknown;
}
