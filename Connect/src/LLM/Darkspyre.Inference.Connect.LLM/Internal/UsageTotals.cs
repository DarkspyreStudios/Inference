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
            ReasoningOutputTokens = Sum(total.ReasoningOutputTokens, usage.ReasoningOutputTokens)
        };

    private static int? Sum(int? left, int? right) => left is >= 0 && right is >= 0
        && (long)left.Value + right.Value <= int.MaxValue ? left.Value + right.Value : null;
}
