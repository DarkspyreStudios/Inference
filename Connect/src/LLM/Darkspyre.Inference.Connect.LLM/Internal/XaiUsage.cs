using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Enriches existing usage with actual xAI cost and hosted-operation observations.</summary>
internal static class XaiUsage
{
    internal static LlmUsage? Enrich(LlmUsage? observed, JsonElement root)
    {
        if (observed is null || LlmJson.GetObject(root, "usage") is not { } usage) return null;
        Dictionary<LlmHostedToolKind, int>? counts = null;
        if (LlmJson.GetObject(usage, "server_side_tool_usage_details") is { } details)
        {
            counts = [];
            foreach ((string field, LlmHostedToolKind kind) in new[]
            {
                ("web_search_calls", LlmHostedToolKind.WebSearch),
                ("x_search_calls", LlmHostedToolKind.SocialSearch),
                ("code_interpreter_calls", LlmHostedToolKind.CodeExecution),
                ("mcp_calls", LlmHostedToolKind.RemoteMcp),
                ("image_generation_calls", LlmHostedToolKind.ImageGeneration)
            })
                if (ReadCount(details, field) is { } count) counts.Add(kind, count);
            // The file_search_calls/document_search_calls pair has no qualified neutral mapping.
        }
        return observed with
        {
            ProviderReportedCostUsd = ReadCost(usage),
            HostedToolCalls = counts,
            HostedToolCallCount = ReadCount(usage, "num_server_side_tools_used"),
            HostedSourcesUsed = ReadCount(usage, "num_sources_used")
        };
    }

    private static int? ReadCount(JsonElement usage, string field) =>
        usage.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int count) && count >= 0 ? count : null;

    private static decimal? ReadCost(JsonElement usage)
    {
        if (!usage.TryGetProperty("cost_in_usd_ticks", out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !BigInteger.TryParse(value.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger ticks)
            || ticks.Sign < 0) return null;
        // Construct the decimal at tick scale; division can round valid observations.
        byte scale = 10;
        BigInteger maximum = new(decimal.MaxValue);
        while (ticks > maximum && scale > 0)
        {
            BigInteger reduced = BigInteger.DivRem(ticks, 10, out BigInteger remainder);
            if (!remainder.IsZero) return null;
            ticks = reduced;
            scale--;
        }
        if (ticks > maximum) return null;
        int[] bits = decimal.GetBits((decimal)ticks);
        return new decimal(bits[0], bits[1], bits[2], false, scale);
    }
}
