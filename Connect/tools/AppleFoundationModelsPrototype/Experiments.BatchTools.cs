using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace AppleFoundationModelsPrototype;

internal static partial class Experiments
{
    internal static readonly string[] BatchNames =
        ["tool-batch-distinct", "tool-batch-identical", "tool-batch-cancel", "tool-concurrent-sessions", "tool-batch-replay"];

    private sealed record BatchCall(uint BridgeId, string Arguments, string Key);
    private sealed record TranscriptCall(int Session, string Id, string Arguments);
    private static BatchCalls? _batchCalls;

    // One recorder belongs to this isolated worker. Each callback is retained independently.
    private sealed class BatchCalls(nint tool)
    {
        private readonly Channel<BatchCall> _arrivals = Channel.CreateUnbounded<BatchCall>();
        private readonly ConcurrentQueue<BatchCall> _all = new();
        private int _count;
        internal BatchCall[] All => [.. _all];

        internal void Record(uint id, string arguments)
        {
            using JsonDocument json = JsonDocument.Parse(arguments);
            var call = new BatchCall(id, arguments, json.RootElement.GetProperty("key").GetString() ?? string.Empty);
            _all.Enqueue(call);
            int count = Interlocked.Increment(ref _count);
            _arrivals.Writer.TryWrite(call);
            if (count > 2)
                Native.FMBridgedToolFinishCall(tool, id, "UNEXPECTED-EXTRA-CALL");
        }

        internal void Fail(Exception error) => _arrivals.Writer.TryComplete(error);

        internal async Task<BatchCall?> NextAsync(TimeSpan timeout)
        {
            using var deadline = new CancellationTokenSource(timeout);
            try { return await _arrivals.Reader.ReadAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return null; }
        }
    }

    private static unsafe NativeReference BatchSession(NativeReference model, NativeReference tool)
    {
        nint pointer = tool.Pointer;
        return new(Native.FMLanguageModelSessionCreateFromSystemLanguageModel(model.Pointer,
            "Use lookup_code for every requested reading. Every reading requires its own call, even when keys are identical. "
            + "Independent calls may be requested together. Never merge keys into one call. Return every resulting code exactly.",
            (nint)(&pointer), 1));
    }

    private static async Task<Evidence> BatchToolsAsync(NativeReference model, string mode)
    {
        bool concurrentSessions = mode == "tool-concurrent-sessions";
        bool replayBatch = mode == "tool-batch-replay";
        bool identical = mode == "tool-batch-identical" || replayBatch;
        bool cancel = mode == "tool-batch-cancel";
        using var schema = new NativeReference(Native.FMGenerationSchemaCreate("LookupArguments", "One lookup per key"));
        using NativeReference key = Property(schema, "key", "string");
        nint pointer = Native.FMBridgedToolCreate("lookup_code", "Returns one current synthetic code for a key.", schema.Pointer,
            ToolCallback, out int code, out nint error);
        string message = Native.TakeString(error);
        if (code != 0) throw new InvalidOperationException($"Tool creation error {code}: {message}");
        using var tool = new NativeReference(pointer);
        var recorder = new BatchCalls(tool.Pointer);
        _batchCalls = recorder;
        string[] prompts = concurrentSessions
            ? ["Look up key alpha once and return its code.", "Look up key bravo once and return its code."]
            : [identical
                ? "Take two separate readings of key sample using two lookup_code calls. Both calls use exactly key sample. Return both codes."
                : "Look up key alpha and key bravo using two independent lookup_code calls. Request both before interpreting the results. Return both codes."];
        var sessions = new List<NativeReference>();
        var operations = new List<NativeOperation>();
        var tasks = new List<NativeReference>();
        try
        {
            foreach (string text in prompts)
            {
                NativeReference session = BatchSession(model, tool);
                sessions.Add(session);
                using NativeReference prompt = Prompt(text);
                var operation = new NativeOperation();
                operations.Add(operation);
                tasks.Add(new NativeReference(Native.FMLanguageModelSessionRespond(
                    session.Pointer, prompt.Pointer, Options, operation.Id, NativeOperation.TextCallback)));
            }

            BatchCall? first = await recorder.NextAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            BatchCall? second = first is null ? null : await recorder.NextAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            JsonNode[] pending = sessions.Select(static session => JsonNode.Parse(Transcript(session))!).ToArray();
            TranscriptCall[] nativeCalls = pending.SelectMany(static (transcript, session) =>
                transcript["transcript"]!["entries"]!.AsArray()
                    .SelectMany(static entry => entry?["toolCalls"]?.AsArray().OfType<JsonNode>() ?? [])
                    .Select(call => new TranscriptCall(session, call["id"]!.GetValue<string>(), call["arguments"]!.GetValue<string>())))
                .ToArray();
            bool twoPending = first is not null && second is not null
                && operations.All(static operation => !operation.Completion.IsCompleted)
                && sessions.All(static session => Native.FMLanguageModelSessionIsResponding(session.Pointer));
            BatchCall[] held = new[] { first, second }.OfType<BatchCall>().ToArray();
            var identity = held.Select(call => new
            {
                call.BridgeId,
                call.Arguments,
                candidates = nativeCalls.Where(native => JsonNode.DeepEquals(JsonNode.Parse(call.Arguments), JsonNode.Parse(native.Arguments))).ToArray()
            }).ToArray();
            bool unambiguous = identity.All(static match => match.candidates.Length == 1);
            bool distinctIds = held.Select(static call => call.BridgeId).Distinct().Count() == held.Length
                && nativeCalls.Select(static call => call.Id).Distinct(StringComparer.Ordinal).Count() == nativeCalls.Length;
            var supplied = new Dictionary<uint, string>();
            var completionOrder = new List<uint>();
            bool? finishedWhileFirstHeld = null;
            var clock = Stopwatch.StartNew();
            if (!twoPending || cancel || replayBatch)
            {
                for (int index = 0; index < operations.Count; index++)
                {
                    operations[index].RequestCancellation();
                    Native.FMTaskCancel(tasks[index].Pointer);
                }
            }
            else
            {
                foreach (BatchCall call in held.Reverse())
                {
                    string result = identical ? $"READING-{call.BridgeId}-729" : call.Key switch
                    {
                        "alpha" => "ORCHID-729",
                        "bravo" => "LANTERN-842",
                        _ => "UNEXPECTED-KEY"
                    };
                    supplied.Add(call.BridgeId, result);
                    completionOrder.Add(call.BridgeId);
                    Native.FMBridgedToolFinishCall(tool.Pointer, call.BridgeId, result);
                    if (completionOrder.Count == 1)
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        finishedWhileFirstHeld = operations.All(static operation => operation.Completion.IsCompleted);
                    }
                }
            }
            NativeReply[] replies = await Task.WhenAll(operations.Select(static operation => operation.WaitAsync())).ConfigureAwait(false);
            double milliseconds = clock.Elapsed.TotalMilliseconds;
            // Both late results after cancellation and duplicates after success must be harmless.
            foreach (BatchCall call in held)
                Native.FMBridgedToolFinishCall(tool.Pointer, call.BridgeId, "LATE-DUPLICATE-RESULT");
            await Task.Delay(100).ConfigureAwait(false);
            JsonNode[] final = sessions.Select(static session => JsonNode.Parse(Transcript(session))!).ToArray();
            if (replayBatch && twoPending)
            {
                bool settled = distinctIds && nativeCalls.Length == 2
                    && replies.All(static reply => reply.Status != 0 && reply.CancellationRequested)
                    && operations.All(static operation => operation.TerminalCallbacks == 1)
                    && sessions.All(static session => !Native.FMLanguageModelSessionIsResponding(session.Pointer));
                if (!settled) return new("unexpected", new { mode, pending, replies, final });
                foreach (NativeReference requestTask in tasks) requestTask.Dispose();
                foreach (NativeReference session in sessions) session.Dispose();
                Evidence replay = await ReplayToolBatchAsync(model, tool, recorder, pending[0], nativeCalls).ConfigureAwait(false);
                return new(replay.Outcome, new
                {
                    mode,
                    twoPending,
                    identity,
                    unambiguous,
                    cancellationReplies = replies,
                    cancellationMs = milliseconds,
                    cancelledTranscript = final,
                    replay
                });
            }
            var outputs = final.SelectMany(static (transcript, session) => transcript["transcript"]!["entries"]!.AsArray()
                .Where(static entry => entry?["role"]?.GetValue<string>() == "tool")
                .Select(entry => new
                {
                    session,
                    id = entry!["toolCallID"]!.GetValue<string>(),
                    text = string.Concat(entry["contents"]!.AsArray().Select(static part => part?["text"]?.GetValue<string>() ?? string.Empty))
                })).ToArray();
            bool resultsRecorded = supplied.Count == 2 && outputs.Length == 2
                && supplied.Values.All(result => outputs.Count(output => output.text == result) == 1);
            bool? resultsCorrelated = unambiguous && supplied.Count == 2
                ? identity.All(match => outputs.Any(output => output.id == match.candidates[0].Id
                    && output.session == match.candidates[0].Session && output.text == supplied[match.BridgeId])) : null;
            bool answersConsumedResults = supplied.Count == 2 && (concurrentSessions
                ? identity.All(match => match.candidates.Length == 1 && supplied.TryGetValue(match.BridgeId, out string? result)
                    && replies[match.candidates[0].Session].Content.Contains(result, StringComparison.Ordinal))
                : supplied.Values.All(result => replies[0].Content.Contains(result, StringComparison.Ordinal)));
            bool idle = sessions.All(static session => !Native.FMLanguageModelSessionIsResponding(session.Pointer));
            bool oneTerminalEach = operations.All(static operation => operation.TerminalCallbacks == 1);
            bool completed = cancel ? replies.All(static reply => reply.CancellationRequested && reply.Status != 0)
                : replies.All(static reply => reply.Status == 0) && resultsRecorded && answersConsumedResults
                    && resultsCorrelated is not false && finishedWhileFirstHeld == false;
            string outcome = !idle || !oneTerminalEach || !distinctIds ? "unexpected"
                : !twoPending || recorder.All.Length != 2 ? "inconclusive"
                : !completed ? "unexpected"
                : !unambiguous && !cancel ? "limitation" : "observed";
            return new(outcome, new
            {
                mode,
                twoPending,
                callbacks = recorder.All,
                distinctIds,
                identity,
                unambiguous,
                callbackDeliveryOrder = held.Select(static call => call.BridgeId).ToArray(),
                pending,
                supplied,
                completionOrder,
                finishedWhileFirstHeld,
                replies,
                milliseconds,
                outputs,
                resultsRecorded,
                resultsCorrelated,
                answersConsumedResults,
                terminalCounts = operations.Select(static operation => operation.TerminalCallbacks).ToArray(),
                idle,
                final
            });
        }
        finally
        {
            for (int index = 0; index < tasks.Count; index++)
            {
                if (!operations[index].Completion.IsCompleted)
                {
                    operations[index].RequestCancellation();
                    Native.FMTaskCancel(tasks[index].Pointer);
                    await operations[index].WaitAsync().ConfigureAwait(false);
                }
            }
            foreach (NativeReference task in tasks) task.Dispose();
            foreach (NativeOperation operation in operations) operation.Dispose();
            foreach (NativeReference session in sessions) session.Dispose();
        }
    }

    private static async Task<Evidence> ReplayToolBatchAsync(NativeReference model, NativeReference tool,
        BatchCalls recorder, JsonNode pending, TranscriptCall[] nativeCalls)
    {
        // Chat calls originate from the complete transcript batch. No callback-to-UUID join is needed.
        var results = nativeCalls.Select((call, index) => new { call.Id, Result = $"BATCH-{index + 1}-729" })
            .ToDictionary(static item => item.Id, static item => item.Result, StringComparer.Ordinal);
        JsonNode replay = pending.DeepClone();
        JsonArray entries = replay["transcript"]!["entries"]!.AsArray();
        foreach (TranscriptCall call in nativeCalls.Reverse())
            entries.Add(JsonSerializer.SerializeToNode(new
            {
                id = call.Id,
                toolCallID = call.Id,
                toolName = "lookup_code",
                role = "tool",
                contents = new[] { new { id = Guid.NewGuid().ToString(), type = "text", text = results[call.Id] } }
            }));
        nint pointer = Native.FMTranscriptCreateFromJSONString(replay.ToJsonString(), out int code, out nint error);
        string message = Native.TakeString(error);
        if (code != 0) return new("limitation", new { reason = "Batch transcript rejected.", code, message, replay });
        using var transcript = new NativeReference(pointer);
        using NativeReference restored = RestoreToolSession(transcript, model, tool);
        bool restoredExactly = JsonNode.DeepEquals(replay, JsonNode.Parse(Transcript(restored)));
        using var prompt = new NativeReference(Native.FMComposedPromptInitialize());
        using var operation = new NativeOperation();
        using var task = new NativeReference(Native.FMLanguageModelSessionRespond(
            restored.Pointer, prompt.Pointer, Options, operation.Id, NativeOperation.TextCallback));
        NativeReply reply = await operation.WaitAsync().ConfigureAwait(false);
        JsonNode final = JsonNode.Parse(Transcript(restored))!;
        JsonArray finalEntries = final["transcript"]!["entries"]!.AsArray();
        bool prefixPreserved = finalEntries.Count >= entries.Count
            && entries.Select((entry, index) => JsonNode.DeepEquals(entry, finalEntries[index])).All(static equal => equal);
        JsonNode[] addedUsers = finalEntries.Skip(entries.Count)
            .Where(static entry => entry?["role"]?.GetValue<string>() == "user").OfType<JsonNode>().ToArray();
        bool acceptedInternalPrompt = addedUsers.Length <= 1
            && addedUsers.All(static entry => entry["contents"] is JsonArray { Count: 0 });
        bool consumedBoth = reply.Status == 0 && results.Values.All(result => reply.Content.Contains(result, StringComparison.Ordinal))
            && !reply.Content.Contains("UNEXPECTED-EXTRA-CALL", StringComparison.Ordinal);
        bool idle = !Native.FMLanguageModelSessionIsResponding(restored.Pointer);
        bool noReexecution = recorder.All.Length == 2;
        return new(!idle || operation.TerminalCallbacks != 1 ? "unexpected"
            : restoredExactly && prefixPreserved && acceptedInternalPrompt && consumedBoth && noReexecution ? "observed" : "limitation", new
            {
                results,
                resultOrder = nativeCalls.Reverse().Select(static call => call.Id).ToArray(),
                restoredExactly,
                prefixPreserved,
                addedUserEntries = addedUsers.Length,
                acceptedInternalPrompt,
                consumedBoth,
                noReexecution,
                reply,
                operation.TerminalCallbacks,
                idle,
                final
            });
    }
}
