using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Emits an observation only when the running usage changes.</summary>
internal sealed class UsageDeltas(bool enabled)
{
    private LlmUsage? _previous;

    public LlmUsageDeltaEvent? Next(LlmUsage? cumulative)
    {
        if (!enabled || cumulative is null || Same(cumulative, _previous))
        {
            return null;
        }

        _previous = cumulative;
        return new(cumulative);
    }

    private static bool Same(LlmUsage current, LlmUsage? previous) => previous is not null
        && current.InputTokens == previous.InputTokens && current.OutputTokens == previous.OutputTokens
        && current.TotalTokens == previous.TotalTokens && current.CachedInputTokens == previous.CachedInputTokens
        && current.CacheWriteInputTokens == previous.CacheWriteInputTokens
        && current.ReasoningOutputTokens == previous.ReasoningOutputTokens
        && current.ProviderReportedCostUsd == previous.ProviderReportedCostUsd
        && current.HostedToolCallCount == previous.HostedToolCallCount && current.HostedSourcesUsed == previous.HostedSourcesUsed
        && (current.HostedToolCalls is null ? previous.HostedToolCalls is null
            : previous.HostedToolCalls is { } counts && current.HostedToolCalls.Count == counts.Count
                && current.HostedToolCalls.All(pair => counts.TryGetValue(pair.Key, out int value) && pair.Value == value));
}
