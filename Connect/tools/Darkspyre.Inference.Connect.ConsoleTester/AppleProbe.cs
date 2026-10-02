using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

internal sealed class AppleProbe(ILlmInference inference,
    Func<LlmAgentSessionOptions, CancellationToken, Task<ILlmAgentSession>> openAgent, string model)
{
    private static readonly JsonElement Schema = JsonElement.Parse("""
        {"type":"object","properties":{"key":{"type":"string"}},"required":["key"],"additionalProperties":false}
        """);

    private static readonly LlmToolDefinition Tool = new()
    {
        Name = "lookup",
        Description = "Returns a synthetic reading for the supplied key.",
        InputSchema = Schema
    };

    public IReadOnlyList<ProbeCase> Cases =>
    [new("text", TextAsync), new("structured", StructuredAsync), new("tools", ToolsAsync),
        new("agent", AgentAsync), new("cancel", CancelAsync)];

    private LlmInferenceRequest Request(string prompt, int cap = 128) => new()
    {
        ModelId = model,
        Messages = [new("user", prompt)],
        MaxOutputTokens = cap
    };

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new ProbeFailure(detail);
    }

    private static void Completed(LlmInferenceResult result)
    {
        Require(result.Status == LlmCompletionStatus.Unknown, $"expected Unknown terminal, got {result.Status}");
        Require(!string.IsNullOrWhiteSpace(result.Text), "terminal text was empty");
        Require(result.ToolCalls.Count == 0, "unexpected tool calls at final completion");
        Require(result.Usage is null && result.FinishReason is null && result.IncompleteReason is null,
            "Apple result invented usage or a finish classification");
    }

    private async Task<string> TextAsync(CancellationToken token)
    {
        Completed(await inference.CompleteAsync(Request("Reply with hello.", 32), token));
        var events = await ReadAsync(inference.StreamAsync(Request("Write a short greeting.", 64), token));
        Require(events.OfType<LlmTextDeltaEvent>().Any(item => item.Delta.Length > 0), "stream emitted no text");
        var result = Terminal(events);
        Completed(result);
        Require(string.Concat(events.OfType<LlmTextDeltaEvent>().Select(item => item.Delta)) == result.Text,
            "streamed text differs from final text");
        return "chat and text streaming; terminal=Unknown, usage unreported";
    }

    private async Task<string> StructuredAsync(CancellationToken token)
    {
        var events = await ReadAsync(inference.StreamAsync(Request("Set key to the word sample.") with
        { StructuredOutput = new() { Name = "sample", Schema = Schema } }, token));
        int snapshots = events.OfType<LlmStructuredOutputSnapshotEvent>().Count();
        Require(snapshots > 0, "no structured snapshots");
        Require(!events.OfType<LlmTextDeltaEvent>().Any(), "structured output emitted text deltas");
        var result = Terminal(events);
        Completed(result);
        Require(result.StructuredOutput is { WasEvaluated: true, IsValid: true }, "final JSON failed original-schema validation");
        return $"snapshots={snapshots}; final schema valid; terminal=Unknown";
    }

    private async Task<string> ToolsAsync(CancellationToken token)
    {
        var request = Request("Use lookup twice, both times with key sample, to obtain two independent readings. Do not invent the readings.", 256)
            with
        { Tools = [Tool] };
        var calls = await inference.CompleteAsync(request, token);
        Require(calls.Status == LlmCompletionStatus.RequiresAction, $"expected tool handoff, got {calls.Status}");
        Require(calls.ToolCalls.Count == 2, $"expected two tool calls, got {calls.ToolCalls.Count}");
        Require(calls.ToolCalls.Select(call => call.Id).Distinct(StringComparer.Ordinal).Count() == 2,
            "identical-argument calls did not retain distinct IDs");
        foreach (LlmClientToolCall item in calls.ToolCalls)
        {
            if (item is not LlmToolCall call) throw new InvalidOperationException("Expected a function tool call.");
            using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            Require(call.Name == Tool.Name && arguments.RootElement.GetProperty("key").GetString() == "sample",
                "model did not select the requested tool and identical arguments");
        }
        var replay = await inference.CompleteAsync(request with
        {
            ToolChoice = new() { Mode = LlmToolChoiceMode.None },
            Messages = [request.Messages[0], new("assistant", calls.Text) { ToolCalls = calls.ToolCalls },
                new("user", "Report the readings already returned. Do not call any tools.")
                { ToolResults = calls.ToolCalls.Reverse().Select((call, i) =>
                    new LlmToolResult(call.Id, LlmToolOutput.FromText($"Synthetic reading {i + 1}: seven"))).ToArray() }]
        }, token);
        Completed(replay);
        return "two identical-argument calls, distinct IDs, reversed results, no-tools replay; terminal=Unknown";
    }

    private async Task<string> AgentAsync(CancellationToken token)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int executed = 0;
        bool duplicate = false;
        await using var session = await openAgent(new()
        {
            ModelId = model,
            Instructions = "Use lookup to retrieve the requested reading. Once its result is present, report it without looking it up again.",
            ExecutionOptions = new() { MaxModelIterations = 4 },
            Tools = [new LlmAgentToolDefinition() { Name = Tool.Name, Description = Tool.Description, InputSchema = Schema }],
            ToolHandler = (call, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                executed++;
                duplicate |= !ids.Add(call.CallId);
                return Task.FromResult(LlmToolOutput.FromText("Synthetic reading: seven"));
            }
        }, token);
        var events = await ReadAsync(session.RunTurnAsync(new()
        { Input = [new LlmAgentTextContent("Look up key sample and report its reading.")] }, token));
        var completions = events.OfType<LlmAgentTurnCompletedEvent>().ToArray();
        Require(completions.Length == 1, "agent did not emit exactly one terminal");
        var context = await session.CaptureContextAsync(cancellationToken: token);
        int retained = context.Entries.Sum(entry => entry.Message.ToolResults.Count);
        Require(!duplicate && retained == executed, $"tool retention mismatch: executions={executed}, retained={retained}, duplicate={duplicate}");
        Require(completions[0].Status == LlmAgentTurnStatus.Unknown,
            $"agent terminal={completions[0].Reason}; executions={executed}, retained={retained}, duplicate={duplicate}");
        Require(executed == 1, $"expected one host lookup; executions={executed}, retained={retained}, duplicate={duplicate}");
        Require(context.Entries.Count > 0 && !string.IsNullOrWhiteSpace(context.Entries[^1].Message.Content), "final agent response was not retained");
        return $"executions={executed}, retained={retained}, duplicate={duplicate}; terminal=Unknown";
    }

    private async Task<string> CancelAsync(CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        int chunks = 0;
        bool cancelled = false;
        try
        {
            await foreach (var item in inference.StreamAsync(Request("Write a long story about a lighthouse.", 512), cancellation.Token))
                if (item is LlmTextDeltaEvent { Delta.Length: > 0 })
                {
                    chunks++;
                    cancellation.Cancel();
                }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !token.IsCancellationRequested)
        { cancelled = true; }
        token.ThrowIfCancellationRequested();
        Require(chunks > 0 && cancelled, "stream did not settle cancellation after a text chunk");
        Completed(await inference.CompleteAsync(Request("Reply with OK.", 16), token));
        return $"cancelled after {chunks} chunk(s); next operation completed with Unknown";
    }

    private static LlmInferenceResult Terminal(IReadOnlyList<LlmStreamEvent> events)
    {
        var terminals = events.OfType<LlmCompletionEvent>().ToArray();
        Require(terminals.Length == 1, "expected exactly one completion event");
        return terminals[0].Result;
    }

    private static async Task<List<T>> ReadAsync<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream) items.Add(item);
        return items;
    }
}
