using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ConversationCompactorTests
{
    // Usable 9,000 tokens: trigger 7,650, target 3,150, part limit 4,500.
    private static readonly CompactionLimits Limits = new(Window: 10_000, Reserve: 1_000, UsableWindow: 9_000,
        TriggerTokens: 7_650, TargetTokens: 3_150);

    [Fact]
    public async Task SummaryReadsTheTranscriptNotTheLastSummary()
    {
        var earlierSummary = new LlmAgentContextEntry("s", ConversationCompactor.SummaryTurnId,
            new("user", "Summary of the earlier conversation:\nEARLIER-SUMMARY"));
        LlmAgentContextEntry[] third = Turn("t3", "turn three " + new string('x', 16_000));
        LlmAgentContextEntry[] current = Turn("t4", "now");
        var context = new LlmAgentContext { Entries = [earlierSummary, .. third, .. current] };
        var transcript = new Transcript([.. Turn("t1", "turn one"), .. Turn("t2", "turn two"), .. third, .. current]);
        var harness = new Harness(transcript);

        CompactionResult result = await harness.Compactor.CompactAsync(context, "model", "t4", new HashSet<string>(), Limits,
            NoStage, CancellationToken.None);

        string material = string.Join("\n", harness.Requests.Select(request => request.Messages.Single().Content));
        Assert.Contains("turn one", material, StringComparison.Ordinal);
        Assert.Contains("turn two", material, StringComparison.Ordinal);
        Assert.DoesNotContain("EARLIER-SUMMARY", material, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Context.Entries, entry => entry.Message.Content.Contains("EARLIER-SUMMARY", StringComparison.Ordinal));
        Assert.Equal([ConversationCompactor.SummaryTurnId, "t4", "t4"], result.Context.Entries.Select(entry => entry.TurnId));
    }

    [Fact]
    public async Task SummaryInputCutsTranscriptToolOutput()
    {
        LlmAgentContextEntry[] older = [.. ToolTurn("t1", new string('q', 20_000)), .. Turn("t2", "older " + new string('z', 16_000))];
        LlmAgentContextEntry[] current = Turn("t3", "now");
        var context = new LlmAgentContext { Entries = [.. older, .. current] };
        var harness = new Harness(new Transcript([.. older, .. current]));

        await harness.Compactor.CompactAsync(context, "model", "t3", new HashSet<string>(), Limits, NoStage, CancellationToken.None);

        string material = string.Join("\n", harness.Requests.Select(request => request.Messages.Single().Content));
        Assert.Contains("[18000 of 20000 characters removed by compaction.]", material, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('q', 1_001), material, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecentTurnsFillTheTargetTheSummaryLeaves()
    {
        // Target 30,000 tokens; a 3,000-word summary takes 4,000, so 13 turns of 2,000 tokens fit.
        var limits = new CompactionLimits(Window: 100_000, Reserve: 10_000, UsableWindow: 90_000, TriggerTokens: 76_500,
            TargetTokens: 30_000);
        LlmAgentContextEntry[] older = [.. Enumerable.Range(1, 20).SelectMany(index => Turn("t" + index, new string('w', 8_000)))];
        var context = new LlmAgentContext { Entries = [.. older, .. Turn("now", "go")] };
        var harness = new Harness(null);

        CompactionResult result = await harness.Compactor.CompactAsync(context, "model", "now", new HashSet<string>(), limits,
            NoStage, CancellationToken.None);

        Assert.Equal([.. Enumerable.Range(8, 13).Select(index => "t" + index), "now"],
            result.Context.Entries.Skip(1).Select(entry => entry.TurnId).Distinct());
    }

    [Fact]
    public async Task TurnTooBigToKeepWholeKeepsItsNewestToolCalls()
    {
        // Target 30,000 tokens; a 3,000-word summary takes 4,000, so 26,000 tokens of the 35,000-token turn fit.
        var limits = new CompactionLimits(Window: 100_000, Reserve: 10_000, UsableWindow: 90_000, TriggerTokens: 76_500,
            TargetTokens: 30_000);
        LlmAgentContextEntry[] big =
        [
            new("big-user", "big", new("user", "do the work")),
            .. Enumerable.Range(1, 70).SelectMany(index => ToolTurn("big", new string('v', 2_000))
                .Select(entry => entry with { Id = entry.Id + index }))
        ];
        var context = new LlmAgentContext { Entries = [.. Turn("old", "earlier"), .. big, .. Turn("now", "go")] };
        var harness = new Harness(null);

        CompactionResult result = await harness.Compactor.CompactAsync(context, "model", "now", new HashSet<string>(), limits,
            NoStage, CancellationToken.None);

        LlmAgentContextEntry[] kept = [.. result.Context.Entries.Where(entry => entry.TurnId == "big")];
        Assert.Equal(104, kept.Length); // 52 calls whose results hold 500 tokens each.
        Assert.NotEmpty(kept[0].Message.ToolCalls);
        Assert.Equal("big-result70", kept[^1].Id);
        string material = string.Join("\n", harness.Requests.Select(request => request.Messages.Single().Content));
        Assert.Contains("do the work", material, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LargeStructuredContentIsCutLikeText()
    {
        string json = "{\"rows\":\"" + new string('s', 6_000) + "\"}";
        LlmAgentContextEntry[] older = ToolTurn("t1", "short");
        older[1] = older[1] with
        {
            Message = older[1].Message with
            {
                ToolResults = [new LlmToolResult("t1-c", LlmToolOutput.FromText("short") with { StructuredContent = JsonElement.Parse(json) })]
            }
        };
        var context = new LlmAgentContext { Entries = [.. older, .. Turn("t2", "now")] };
        var harness = new Harness(null);

        CompactionResult result = await harness.Compactor.CompactAsync(context, "model", "t2", new HashSet<string>(), Limits,
            NoStage, CancellationToken.None);

        LlmToolOutput output = Assert.IsType<LlmToolResult>(Assert.Single(result.Context.Entries[1].Message.ToolResults)).Output;
        Assert.Null(output.StructuredContent);
        Assert.Contains(output.Parts.OfType<LlmToolOutputText>(), part => part.Text.Contains("characters removed by compaction.]", StringComparison.Ordinal));
        Assert.Equal(1, result.ShortenedToolOutputCount);
    }

    [Fact]
    public async Task LongTranscriptIsSummarizedInParts()
    {
        LlmAgentContextEntry[] older = [.. Enumerable.Range(1, 10).SelectMany(index => Turn("t" + index, $"turn {index} " + new string('y', 8_000)))];
        LlmAgentContextEntry[] current = Turn("now", "continue");
        var context = new LlmAgentContext { Entries = [.. older, .. current] };
        var harness = new Harness(new Transcript([.. older, .. current]));

        CompactionResult result = await harness.Compactor.CompactAsync(context, "model", "now", new HashSet<string>(), Limits,
            NoStage, CancellationToken.None);

        Assert.True(harness.Requests.Count > 1);
        Assert.All(harness.Requests, request =>
            Assert.InRange(Harness.Tokens(request.Messages.Single().Content), 0, Limits.UsableWindow / 2));
        string summary = result.Context.Entries[0].Message.Content;
        Assert.StartsWith("Summary of the earlier conversation:\n", summary, StringComparison.Ordinal);
        Assert.Equal(Enumerable.Range(1, harness.Requests.Count).Select(index => "summary " + index),
            summary["Summary of the earlier conversation:\n".Length..].Split("\n\n"));
        Assert.Equal(20, result.DroppedMessageCount);
        Assert.Equal(harness.Requests.Count * 2, result.Usage!.TotalTokens);
    }

    [Fact]
    public async Task SummaryRequestCarriesNoToolSettings()
    {
        LlmAgentContextEntry[] older = Turn("t1", "older " + new string('z', 16_000));
        var context = new LlmAgentContext { Entries = [.. older, .. Turn("t2", "now")] };
        var harness = new Harness(transcript: null);

        await harness.Compactor.CompactAsync(context, "model", "t2", new HashSet<string>(), Limits, NoStage, CancellationToken.None);

        LlmChatRequest request = Assert.Single(harness.Requests);
        Assert.Empty(request.Tools);
        Assert.Null(request.ToolChoice);
        Assert.Null(request.AllowParallelToolCalls);
        Assert.Null(request.StructuredOutput);
        Assert.Null(request.MaxOutputTokens);
        Assert.Matches(@"Write at most \d+ words\.$", request.Instructions);
    }

    [Fact]
    public async Task OnlyCurrentTurnProtectedResultsStayWhole()
    {
        string large = new('r', 6_000);
        LlmAgentContextEntry[] earlier = ToolTurn("t1", large);
        LlmAgentContextEntry[] current = ToolTurn("t2", large);
        var context = new LlmAgentContext { Entries = [.. earlier, .. current] };
        var harness = new Harness(transcript: null);
        // The caller protects the current turn's protected results only (5.11.3).
        var protectedIds = new HashSet<string> { current[1].Id };

        CompactionResult result = await harness.Compactor.CompactAsync(context, "model", "t2", protectedIds, Limits,
            NoStage, CancellationToken.None);

        Assert.Empty(harness.Requests); // Shortening alone reaches the target.
        Assert.Null(result.DroppedMessageCount);
        Assert.Equal(1, result.ShortenedToolOutputCount);
        Assert.Equal(large, ResultText(result.Context.Entries.Single(entry => entry.Id == current[1].Id)));
        string shortened = ResultText(result.Context.Entries.Single(entry => entry.Id == earlier[1].Id));
        Assert.Equal(new string('r', 1_000) + "[4000 of 6000 characters removed by compaction.]" + new string('r', 1_000), shortened);
    }

    [Fact]
    public async Task CompactionThatCannotFitFailsWithWhy()
    {
        var harness = new Harness(transcript: null);
        var oversized = new LlmAgentContext { Entries = [.. Turn("t1", "older " + new string('o', 4_000)), .. Turn("t2", new string('c', 40_000))] };

        var failure = await Assert.ThrowsAsync<CompactionFailedException>(() => harness.Compactor.CompactAsync(oversized, "model",
            "t2", new HashSet<string>(), Limits, NoStage, CancellationToken.None));

        Assert.StartsWith("Compaction could not bring this request under the limit: ", failure.Message, StringComparison.Ordinal);
        Assert.Contains("in a 9000-token usable window. The current turn alone measures 10000 tokens.", failure.Message, StringComparison.Ordinal);

        // A result between the trigger and the usable window is sent, not refused.
        var tight = new LlmAgentContext { Entries = [.. Turn("t1", "older " + new string('o', 4_000)), .. Turn("t2", new string('c', 31_000))] };
        CompactionResult result = await harness.Compactor.CompactAsync(tight, "model", "t2", new HashSet<string>(), Limits,
            NoStage, CancellationToken.None);
        Assert.InRange(Harness.Measure(result.Context), Limits.TriggerTokens, Limits.UsableWindow - 1);
    }

    private static ValueTask NoStage(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private static LlmAgentContextEntry[] Turn(string turnId, string text) =>
        [new(turnId + "-user", turnId, new("user", text)), new(turnId + "-answer", turnId, new("assistant", "ok"))];

    private static LlmAgentContextEntry[] ToolTurn(string turnId, string output) =>
    [
        new(turnId + "-call", turnId, new("assistant", "") { ToolCalls = [new LlmToolCall(turnId + "-c", "fetch", "{}")] }),
        new(turnId + "-result", turnId, new("user", "") { ToolResults = [new LlmToolResult(turnId + "-c", LlmToolOutput.FromText(output))] })
    ];

    private static string ResultText(LlmAgentContextEntry entry) =>
        Assert.IsType<LlmToolOutputText>(Assert.Single(Assert.IsType<LlmToolResult>(Assert.Single(entry.Message.ToolResults)).Output.Parts)).Text;

    /// <summary>Four characters per token over message text and tool-result text; side requests answer "summary N".</summary>
    private sealed class Harness(ILlmConversationTranscript? transcript)
    {
        public List<LlmChatRequest> Requests { get; } = [];

        public ConversationCompactor Compactor => new(
            (context, _) => ValueTask.FromResult(Measure(context)),
            (request, _) =>
            {
                Requests.Add(request);
                return Task.FromResult(new LlmChatResult { Diagnostics = new(200, null, null), Text = "summary " + Requests.Count, Status = LlmCompletionStatus.Completed, Usage = new(1, 1, 2) });
            },
            (text, _) => ValueTask.FromResult<long?>(Tokens(text)),
            transcript);

        public static long Tokens(string text) => text.Length / 4;

        public static long Measure(LlmAgentContext context) => context.Entries.Sum(entry => Tokens(entry.Message.Content)
            + entry.Message.ToolResults.OfType<LlmToolResult>().SelectMany(result => result.Output.Parts)
                .OfType<LlmToolOutputText>().Sum(part => Tokens(part.Text)));
    }

    private sealed class Transcript(IReadOnlyList<LlmAgentContextEntry> entries) : ILlmConversationTranscript
    {
        // Two entries per page, so the compactor follows the cursor.
        public Task<LlmTranscriptPage> ReadAsync(string? cursor, int limit, CancellationToken cancellationToken = default)
        {
            int start = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            int next = Math.Min(entries.Count, start + 2);
            return Task.FromResult(new LlmTranscriptPage([.. entries.Skip(start).Take(next - start)],
                next < entries.Count ? next.ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
        }
    }
}
