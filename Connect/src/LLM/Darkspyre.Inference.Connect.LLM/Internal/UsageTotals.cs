using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Sums distinct calls without treating unreported or overflowing counts as zero.</summary>
internal static class UsageTotals
{
    public static LlmUsage? Add(LlmUsage? total, LlmUsage? usage, bool first) => first ? usage
        : total is null || usage is null ? null : new(
            Sum(total.InputTokens, usage.InputTokens), Sum(total.OutputTokens, usage.OutputTokens),
            Sum(total.TotalTokens, usage.TotalTokens))
        {
            CachedInputTokens = Sum(total.CachedInputTokens, usage.CachedInputTokens),
            CacheWriteInputTokens = Sum(total.CacheWriteInputTokens, usage.CacheWriteInputTokens),
            ReasoningOutputTokens = Sum(total.ReasoningOutputTokens, usage.ReasoningOutputTokens),
            ProviderReportedCostUsd = Sum(total.ProviderReportedCostUsd, usage.ProviderReportedCostUsd),
            HostedToolCalls = Sum(total.HostedToolCalls, usage.HostedToolCalls),
            HostedToolCallCount = Sum(total.HostedToolCallCount, usage.HostedToolCallCount),
            HostedSourcesUsed = Sum(total.HostedSourcesUsed, usage.HostedSourcesUsed)
        };

    private static int? Sum(int? left, int? right) => left is >= 0 && right is >= 0
        && (long)left.Value + right.Value <= int.MaxValue ? left.Value + right.Value : null;

    private static decimal? Sum(decimal? left, decimal? right)
    {
        if (left is null || right is null) return null;
        try { return checked(left.Value + right.Value); }
        catch (OverflowException) { return null; }
    }

    private static Dictionary<LlmHostedToolKind, int>? Sum(
        IReadOnlyDictionary<LlmHostedToolKind, int>? left, IReadOnlyDictionary<LlmHostedToolKind, int>? right)
    {
        if (left is null || right is null) return null;
        var counts = new Dictionary<LlmHostedToolKind, int>();
        foreach ((LlmHostedToolKind kind, int value) in left)
            if (right.TryGetValue(kind, out int other) && Sum(value, other) is { } sum) counts.Add(kind, sum);
        return counts;
    }
}
