using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ClaudeStreamUsageTests
{
    [Fact]
    public void DistinctCallsReplaceCountsAndKeepUnreportedOutputUnknown()
    {
        var ledger = new ClaudeStreamUsage(true);
        Assert.Equal(Usage(10, null, 1, 2), ledger.Observe(Start("m1", 7, 1, 2))?.Cumulative);
        Assert.Equal(Usage(10, 3, 1, 2), ledger.Observe(Delta(3))?.Cumulative);
        Assert.Null(ledger.Observe(Delta(3)));
        Assert.Null(ledger.Observe(Start("m1", 999, 0, 0)));
        Assert.Equal(Usage(10, 5, 1, 2), ledger.Observe(Delta(5))?.Cumulative);
        Assert.Null(ledger.Observe(Stop()));
        Assert.Null(ledger.Observe(Start("m1", 999, 0, 0)));
        Assert.Null(ledger.Observe(Delta(999)));

        Assert.Equal(Usage(30, null, 1, 2), ledger.Observe(Start("m2", 20, 0, 0))?.Cumulative);
        Assert.Equal(Usage(30, 9, 1, 2), ledger.Observe(Delta(4))?.Cumulative);
        Assert.Equal(Usage(34, 10, 3, 3), ledger.Observe(Json(new
        {
            type = "stream_event",
            @event = new { type = "message_delta", usage = new { input_tokens = 21, output_tokens = 5, cache_read_input_tokens = 2, cache_creation_input_tokens = 1 } }
        }))?.Cumulative);
    }

    [Fact]
    public void ReplayAndSubagentsDoNotEnterTheMainLoopLedger()
    {
        var accounted = new HashSet<string>(StringComparer.Ordinal) { "previous" };
        var ledger = new ClaudeStreamUsage(true, accounted);
        Assert.Null(ledger.Observe(Start("previous", 90, 0, 0)));
        Assert.Null(ledger.Observe(Delta(50)));
        Assert.Null(ledger.Observe(Start("subagent", 90, 0, 0, parent: "tool-1")));
        Assert.Null(ledger.Observe(Start("replayed", 90, 0, 0, replay: true)));
        Assert.Equal(Usage(0, null, 0, 0), ledger.Observe(Start("current", 0, 0, 0))?.Cumulative);
        Assert.Equal(Usage(0, 0, 0, 0), ledger.Observe(Delta(0))?.Cumulative);
        Assert.Collection(accounted.Order(), item => Assert.Equal("current", item), item => Assert.Equal("previous", item));

        var next = new ClaudeStreamUsage(true, accounted);
        Assert.Null(next.Observe(Start("current", 0, 0, 0)));
        Assert.Null(next.Observe(Delta(50)));
        Assert.Equal(Usage(2, null, 0, 0), next.Observe(Start("new", 2, 0, 0))?.Cumulative);
    }

    [Fact]
    public void CompletionStopsObservationsAndDoesNotInventFinalUsage()
    {
        var ledger = new ClaudeStreamUsage(true);
        Assert.NotNull(ledger.Observe(Start("m1", 7, 1, 2)));
        ledger.Finish();
        Assert.Null(ledger.Observe(Delta(30)));
        Assert.Null(ledger.Observe(Start("m2", 20, 0, 0)));
        Assert.Null(ClaudeStreamUsage.ReadFinal(Json(new { type = "result" })));
        Assert.Equal(Usage(0, 0, 0, 0), ClaudeStreamUsage.ReadFinal(Json(new
        {
            type = "result",
            usage = new { input_tokens = 0, output_tokens = 0, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 }
        })));
        Assert.Equal(Usage(14, 6, 3, 4), ClaudeStreamUsage.ReadFinal(Json(new
        {
            type = "result",
            usage = new { input_tokens = 7, output_tokens = 6, cache_read_input_tokens = 3, cache_creation_input_tokens = 4 }
        })));
    }

    [Fact]
    public void DisabledObservationsStayDisabled()
    {
        var ledger = new ClaudeStreamUsage(false);
        Assert.Null(ledger.Observe(Start("m1", 7, 0, 0)));
        Assert.Null(ledger.Observe(Delta(2)));
    }

    [Fact]
    public void UsageBeforeAnIdentifiedCallIsAProtocolFailure()
    {
        var ledger = new ClaudeStreamUsage(true);
        Assert.Throws<JsonException>(() => ledger.Observe(Delta(1)));
        Assert.Throws<JsonException>(() => ledger.Observe(Json(new
        {
            type = "stream_event",
            @event = new { type = "message_start", message = new { usage = new { input_tokens = 1 } } }
        })));
    }

    private static JsonElement Start(string id, int input, int cached, int written, string? parent = null, bool replay = false) => Json(new
    {
        type = "stream_event",
        parent_tool_use_id = parent,
        isReplay = replay,
        @event = new { type = "message_start", message = new { id, usage = new { input_tokens = input, cache_read_input_tokens = cached, cache_creation_input_tokens = written, output_tokens = 1 } } }
    });

    private static JsonElement Delta(int output) => Json(new
    {
        type = "stream_event",
        @event = new { type = "message_delta", usage = new { output_tokens = output } }
    });

    private static JsonElement Stop() => Json(new { type = "stream_event", @event = new { type = "message_stop" } });
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static LlmUsage Usage(int input, int? output, int cached, int written) => new(input, output, output is { } count ? input + count : null)
    { CachedInputTokens = cached, CacheWriteInputTokens = written };
}
