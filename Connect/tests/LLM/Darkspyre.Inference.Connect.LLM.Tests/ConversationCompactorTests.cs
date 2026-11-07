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
    public async Task SummaryFoldsTheEarlierSummaryAndNewTurnsNotTheTranscript()
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
        Assert.Contains("EARLIER-SUMMARY", material, StringComparison.Ordinal);
        Assert.Contains("turn three", material, StringComparison.Ordinal);
        Assert.DoesNotContain("turn one", material, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Context.Entries, entry => entry.Message.Content.Contains("EARLIER-SUMMARY", StringComparison.Ordinal));
        Assert.Equal([ConversationCompactor.SummaryTurnId, "t4", "t4"], result.Context.Entries.Select(entry => entry.TurnId));
        Assert.Equal(3, result.DroppedMessageCount);
    }

    private static ValueTask NoStage(string text, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private static LlmAgentContextEntry[] Turn(string turnId, string text) =>
        [new(turnId + "-user", turnId, new("user", text)), new(turnId + "-answer", turnId, new("assistant", "ok"))];

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
