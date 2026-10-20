using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>A Grok model's reasoning levels, argument streaming, idle timeout and backend search facts.</summary>
internal sealed record XaiModelProfile(IReadOnlySet<LlmReasoningLevel> ReasoningLevels, LlmReasoningLevel? DefaultReasoningLevel,
    bool StreamsToolCalls, TimeSpan? IdleTimeout, bool SearchesOnBackend);

/// <summary>Grok model facts, read from the subscription proxy's model list.</summary>
internal static class XaiModelProfiles
{
    private static readonly XaiModelProfile Unknown = new(LlmReasoningEffort.Levels(), null, false, null, false);

    /// <summary>
    /// The model's levels from its catalog entry's reasoning_efforts, each an object whose value names the level and
    /// whose default marks the level the proxy uses when a request names none. A model the catalog does not list, or one
    /// with no reasoning_efforts, offers no levels.
    /// </summary>
    internal static XaiModelProfile Get(LlmConnection connection, string? modelId)
    {
        if (XaiCatalog.FindModel(connection.Catalog, modelId)?.CatalogEntry is not { } catalog) return Unknown;
        bool streams = XaiFields.TryGet(catalog, "stream_tool_calls", "streamToolCalls", out JsonElement streaming)
            && streaming.ValueKind == JsonValueKind.True;
        bool searches = XaiFields.TryGet(catalog, "supports_backend_search", "supportsBackendSearch", out JsonElement search)
            && search.ValueKind == JsonValueKind.True;
        TimeSpan? idle = XaiFields.TryGet(catalog, "inference_idle_timeout_secs", "inferenceIdleTimeoutSecs", out JsonElement timeout)
            && timeout.ValueKind == JsonValueKind.Number && timeout.TryGetInt32(out int seconds) && seconds > 0
            ? TimeSpan.FromSeconds(Math.Max(10, seconds)) : null;
        if (!catalog.TryGetProperty("reasoning_efforts", out JsonElement efforts) || efforts.ValueKind != JsonValueKind.Array)
            return Unknown with { StreamsToolCalls = streams, IdleTimeout = idle, SearchesOnBackend = searches };
        var levels = new HashSet<LlmReasoningLevel>();
        LlmReasoningLevel? defaultLevel = null;
        foreach (JsonElement effort in efforts.EnumerateArray())
        {
            if (effort.ValueKind != JsonValueKind.Object || !effort.TryGetProperty("value", out JsonElement value)
                || value.ValueKind != JsonValueKind.String || Level(value.GetString()) is not { } level)
                continue;
            levels.Add(level);
            if (effort.TryGetProperty("default", out JsonElement isDefault) && isDefault.ValueKind == JsonValueKind.True)
                defaultLevel = level;
        }
        return new(levels.ToFrozenSet(), defaultLevel, streams, idle, searches);
    }

    internal static LlmReasoningLevel? MapReasoning(IReadOnlySet<LlmReasoningLevel> offered, LlmReasoningLevel? requested)
    {
        if (requested is null) return null;
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
}
