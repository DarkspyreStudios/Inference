using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedUsageTests
{
    [Fact]
    public void DistinctCallsSumOnlyKnownFactsAndIntersectNamedCounts()
    {
        var first = new LlmUsage(1, 2, 3)
        {
            ProviderReportedCostUsd = 0.0000000001m,
            HostedToolCallCount = 5,
            HostedSourcesUsed = 0,
            HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [LlmHostedToolKind.WebSearch] = 2, [LlmHostedToolKind.RemoteMcp] = 1 }
        };
        var second = first with
        {
            ProviderReportedCostUsd = 0.0000000002m,
            HostedToolCallCount = 7,
            HostedSourcesUsed = 10,
            HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [LlmHostedToolKind.WebSearch] = 3, [LlmHostedToolKind.CodeExecution] = 2 }
        };
        var result = UsageTotals.Add(first, second, false)!;
        Assert.Equal(0.0000000003m, result.ProviderReportedCostUsd);
        Assert.Equal(12, result.HostedToolCallCount);
        Assert.Equal(10, result.HostedSourcesUsed);
        Assert.Equal(new KeyValuePair<LlmHostedToolKind, int>(LlmHostedToolKind.WebSearch, 5), Assert.Single(result.HostedToolCalls!));
        Assert.Equal(6, result.TotalTokens);
        Assert.Null(result.ReasoningOutputTokens);
    }

    [Fact]
    public void MissingAndOverflowingFactsStayUnknownWithoutLosingOtherFields()
    {
        var first = new LlmUsage(1, 2, 3)
        {
            ProviderReportedCostUsd = decimal.MaxValue,
            HostedToolCallCount = int.MaxValue,
            HostedSourcesUsed = 0,
            HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [LlmHostedToolKind.WebSearch] = int.MaxValue, [LlmHostedToolKind.CodeExecution] = 0 }
        };
        var second = first with
        {
            ProviderReportedCostUsd = 1m,
            HostedToolCallCount = 1,
            HostedSourcesUsed = 0,
            HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [LlmHostedToolKind.WebSearch] = 1, [LlmHostedToolKind.CodeExecution] = 0 }
        };
        var result = UsageTotals.Add(first, second, false)!;
        Assert.Null(result.ProviderReportedCostUsd);
        Assert.Null(result.HostedToolCallCount);
        Assert.Equal(0, result.HostedSourcesUsed);
        Assert.Equal(new KeyValuePair<LlmHostedToolKind, int>(LlmHostedToolKind.CodeExecution, 0), Assert.Single(result.HostedToolCalls!));
        result = UsageTotals.Add(result, new(1, 2, 3), false)!;
        Assert.Null(result.HostedToolCalls);
        Assert.Null(result.HostedSourcesUsed);
        Assert.Null(result.ProviderReportedCostUsd);
        Assert.Equal(9, result.TotalTokens);
        Assert.Null(UsageTotals.Add(first, null, false));
        Assert.Null(UsageTotals.Add(null, first, false));
        Assert.Null(UsageTotals.Add(first, null, true));
    }

    [Fact]
    public void FirstObservationKeepsZeroAndEmptyButNotNegativeCounts()
    {
        var invalid = new LlmUsage(0, 0, 0)
        {
            ProviderReportedCostUsd = -1m,
            HostedToolCallCount = -1,
            HostedSourcesUsed = -1,
            HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [LlmHostedToolKind.WebSearch] = -1 }
        };
        var result = UsageTotals.Add(null, invalid, true)!;
        Assert.Null(result.HostedToolCallCount);
        Assert.Null(result.HostedSourcesUsed);
        Assert.Empty(result.HostedToolCalls!);
        // The shared contract does not invent a provider-specific policy for signed costs.
        Assert.Equal(-1m, result.ProviderReportedCostUsd);
        var zero = invalid with { HostedToolCallCount = 0, HostedSourcesUsed = 0, HostedToolCalls = new Dictionary<LlmHostedToolKind, int>() };
        result = UsageTotals.Add(zero, zero, false)!;
        Assert.Equal(0, result.HostedToolCallCount);
        Assert.Equal(0, result.HostedSourcesUsed);
        Assert.Empty(result.HostedToolCalls!);
        Assert.Null(UsageTotals.Add(zero, zero with { HostedToolCalls = null }, false)!.HostedToolCalls);
    }

    [Fact]
    public void NamedCountsSnapshotInputAndDuplicateDetectionUsesContents()
    {
        var counts = new Dictionary<LlmHostedToolKind, int> { [LlmHostedToolKind.WebSearch] = 1, [LlmHostedToolKind.RemoteMcp] = 2 };
        var usage = new LlmUsage(null, null, null) { HostedToolCalls = counts };
        counts[LlmHostedToolKind.WebSearch] = 999;
        Assert.Equal(1, usage.HostedToolCalls![LlmHostedToolKind.WebSearch]);
        var deltas = new UsageDeltas(true);
        Assert.NotNull(deltas.Next(usage));
        Assert.Null(deltas.Next(usage with
        { HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [LlmHostedToolKind.RemoteMcp] = 2, [LlmHostedToolKind.WebSearch] = 1 } }));
        Assert.NotNull(deltas.Next(usage with { HostedToolCalls = null }));
        Assert.NotNull(deltas.Next(usage with { HostedToolCalls = new Dictionary<LlmHostedToolKind, int>() }));
        Assert.NotNull(deltas.Next(usage with { HostedToolCallCount = 0 }));
        Assert.NotNull(deltas.Next(usage with { HostedSourcesUsed = 0 }));
        Assert.NotNull(deltas.Next(usage with { ProviderReportedCostUsd = 0m }));
    }

    [Fact]
    public void OwnedCommonAndConnectGraphsPreservePreciseCostAndIndependentCounts()
    {
        var usage = new LlmUsage(null, 0, null)
        {
            ProviderReportedCostUsd = 1.0000000001m,
            HostedToolCallCount = 8,
            HostedSourcesUsed = 0,
            HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [LlmHostedToolKind.WebSearch] = 2 }
        };
        var observation = JsonSerializer.Deserialize(JsonSerializer.Serialize(new LlmUsageDeltaEvent(usage), InferenceJson.UsageDelta), InferenceJson.UsageDelta)!;
        var result = new LlmChatResult { Text = "done", Status = LlmCompletionStatus.Completed, Usage = usage, Diagnostics = new(200, null, null) };
        var chat = JsonSerializer.Deserialize(JsonSerializer.Serialize(result, ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;
        foreach (LlmUsage value in new[] { observation.Cumulative, chat.Usage! })
        {
            Assert.Equal(1.0000000001m, value.ProviderReportedCostUsd);
            Assert.Equal(8, value.HostedToolCallCount);
            Assert.Equal(0, value.HostedSourcesUsed);
            Assert.Equal(2, value.HostedToolCalls![LlmHostedToolKind.WebSearch]);
            Assert.Null(value.InputTokens);
            Assert.Equal(0, value.OutputTokens);
        }
    }
}
