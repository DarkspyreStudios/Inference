using System.Text.Json;
using Darkspyre.Inference.Connect.LLM;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.ConsoleTester;

/// <summary>Live public-contract proof, using in-memory JSON instead of a second session store.</summary>
internal static class ManagedAgentProbe
{
    public static async Task<int> RunAsync(LlmAgentProviderService agents, string connectionId,
        string modelId, CancellationToken cancellationToken)
    {
        int toolCalls = 0;
        var options = new LlmAgentSessionOptions
        {
            ModelId = modelId,
            WorkingDirectory = Environment.CurrentDirectory,
            Instructions = "Follow the user's request.",
            Tools = [Program.CreateAttachmentTool(supportsDocuments: true)],
            Handlers = new()
            {
                ToolHandler = async (call, token) =>
            {
                Interlocked.Increment(ref toolCalls);
                return await Program.ExecuteToolAsync(call, token);
            }
            }
        };
        await using var session = await agents.StartSessionAsync(connectionId, options, cancellationToken);
        var result = await ReadAsync(session.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent("Call darkspyre_inference_connect_attachment with kind document exactly once. Quote its text.")],
            ReasoningLevel = LlmReasoningLevel.Low,
            Instructions = "Use the requested tool. Include the exact document text in the final answer."
        }, cancellationToken));
        RequireCompleted(result, "attachment smoke test document");
        if (toolCalls != 1) { throw new InvalidOperationException("Expected exactly one document tool call."); }
        Console.WriteLine("PASS: managed tool loop with document bytes and per-run instructions.");

        var full = await session.CaptureContextAsync(new() { IncludeAttachmentData = true }, cancellationToken);
        var omitted = await session.CaptureContextAsync(cancellationToken: cancellationToken);
        if (!omitted.Entries.Any(entry => entry.Message.OmittedAttachments.Count > 0))
        { throw new InvalidOperationException("Default capture must describe omitted attachments."); }
        var persisted = JsonSerializer.Deserialize<LlmAgentContext>(JsonSerializer.Serialize(full))!;
        await using var reopened = await agents.StartSessionAsync(connectionId, options with
        { Context = persisted, ThreadId = session.ThreadId, SessionId = session.SessionId }, cancellationToken);
        result = await ReadAsync(reopened.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent("Without calling any tools, quote the text of the document from earlier.")],
            ReasoningLevel = LlmReasoningLevel.Low
        }, cancellationToken));
        RequireCompleted(result, "attachment smoke test document");
        if (toolCalls != 1) { throw new InvalidOperationException("Reopen replayed an already completed tool."); }
        Console.WriteLine("PASS: JSON capture/reopen, retained attachment access, no tool replay.");

        await using var fork = await agents.StartSessionAsync(connectionId, options with { Context = persisted, SessionId = session.SessionId }, cancellationToken);
        if (fork.ThreadId == session.ThreadId) { throw new InvalidOperationException("A fork requires new identity."); }
        result = await ReadAsync(fork.RunTurnAsync(new()
        {
            Input = [new LlmAgentTextContent("Reply with FORK only.")],
            Instructions = "Do not call tools. Reply with FORK only.",
            ReasoningLevel = LlmReasoningLevel.Low
        }, cancellationToken));
        RequireCompleted(result, "FORK");
        Console.WriteLine("PASS: independent host-context fork.");

        await using var compacting = await agents.StartSessionAsync(connectionId, options with
        {
            Tools = [],
            Context = new()
            {
                Entries = [new("old", "old-turn", new("user", string.Join(' ', Enumerable.Repeat("Earlier irrelevant context.", 3000))))]
            },
            ExecutionOptions = new()
            {
                ContextPolicy = new()
                { MaxContextTokens = 2048, ReservedOutputTokens = 256, SafetyMarginTokens = 32 }
            }
        }, cancellationToken);
        var turn = new LlmAgentTurnRequest
        {
            Input = [new LlmAgentTextContent("Confirm the current request.")],
            Instructions = "Reply with COMPACTED only.",
            ReasoningLevel = LlmReasoningLevel.Low
        };
        var preview = await compacting.GetContextStatusAsync(turn, cancellationToken);
        if (preview.NeedsCompaction != true) { throw new InvalidOperationException("Expected low-cap context pressure."); }
        result = await ReadAsync(compacting.RunTurnAsync(turn, cancellationToken));
        var pause = result.OfType<LlmAgentCompactionRequiredEvent>().Single();
        if (!pause.CanCompactHistory || result.OfType<LlmAgentTurnCompletedEvent>().Any())
        { throw new InvalidOperationException("Compaction must suspend, not complete, the logical run."); }
        var captured = await compacting.CaptureContextAsync(new() { IncludeAttachmentData = true }, cancellationToken);
        // This tiny fixture replaces deliberately irrelevant history. Real summarization belongs to Agent DS.
        var replacement = captured with { Entries = captured.Entries.Where(entry => pause.ProtectedEntryIds.Contains(entry.Id)).ToArray() };
        result = await ReadAsync(compacting.ContinueTurnAsync(new(pause.TurnId, pause.PauseId, captured.Revision, replacement), cancellationToken));
        RequireCompleted(result, "COMPACTED");
        if (result.Any(item => item.TurnId != pause.TurnId)) { throw new InvalidOperationException("Continuation changed turn identity."); }
        var final = await compacting.CaptureContextAsync(cancellationToken: cancellationToken);
        if (final.Entries.Count(entry => entry.Message.Role == "user") != 1)
        { throw new InvalidOperationException("Continuation duplicated the original prompt."); }
        Console.WriteLine("PASS: prospective budget, host compaction, same-run continuation, retained override, no duplicate prompt.");
        return 0;
    }

    private static async Task<List<LlmAgentEvent>> ReadAsync(IAsyncEnumerable<LlmAgentEvent> stream)
    {
        var events = new List<LlmAgentEvent>();
        await foreach (var item in stream) { events.Add(item); }
        return events;
    }

    private static void RequireCompleted(IReadOnlyList<LlmAgentEvent> events, string expected)
    {
        var final = events.OfType<LlmAgentTurnCompletedEvent>().SingleOrDefault();
        string text = string.Join("\n", events.OfType<LlmAgentMessageCompletedEvent>().Select(item => item.Text));
        if (final?.Status != LlmAgentTurnStatus.Completed || !text.Contains(expected, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("Managed live probe did not complete with its expected fixture result."); }
    }
}
