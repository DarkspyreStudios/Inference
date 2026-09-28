using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AppleFoundationModelsPrototype;

internal sealed record Evidence(string Outcome, object Details);

internal static class Experiments
{
    internal const string Options = "{\"temperature\":0,\"maximum_response_tokens\":128}";
    internal static readonly string[] Names =
    ["availability", "text", "stream", "history", "schema", "schema-nested", "standard-schema",
        "token-limit", "cancel-before", "cancel-stream", "tool-delayed", "tool-immediate", "tool-cancel"];
    internal static readonly string[] LifecycleNames =
        ["cancel-before", "cancel-stream", "tool-delayed", "tool-immediate", "tool-duplicate", "tool-cancel", "tool-cancel-late"];

    internal static async Task<Evidence> RunAsync(string name)
    {
        using var model = new NativeReference(Native.FMSystemLanguageModelGetDefault());
        if (!Native.FMSystemLanguageModelIsAvailable(model.Pointer, out int reason))
            return new("unavailable", new { reason });
        return name switch
        {
            "availability" => new("observed", new { available = true, contextSize = Native.FMSystemLanguageModelGetContextSize(model.Pointer) }),
            "text" => await TextAsync(model).ConfigureAwait(false),
            "stream" => await StreamAsync(model, false).ConfigureAwait(false),
            "history" => await HistoryAsync(model).ConfigureAwait(false),
            "schema" => await SchemaAsync(model, false).ConfigureAwait(false),
            "schema-nested" => await SchemaAsync(model, true).ConfigureAwait(false),
            "standard-schema" => await StandardSchemaAsync(model).ConfigureAwait(false),
            "token-limit" => await TokenLimitAsync(model).ConfigureAwait(false),
            "cancel-before" => await CancelBeforeAsync(model).ConfigureAwait(false),
            "cancel-stream" => await StreamAsync(model, true).ConfigureAwait(false),
            "tool-delayed" or "tool-immediate" or "tool-duplicate" or "tool-cancel" or "tool-cancel-late" or "tool-roundtrip"
                => await ToolAsync(model, name).ConfigureAwait(false),
            _ => throw new ArgumentException("Unknown experiment.", nameof(name))
        };
    }

    private static NativeReference Session(NativeReference model, string? instructions = null) =>
        new(Native.FMLanguageModelSessionCreateFromSystemLanguageModel(model.Pointer, instructions, 0, 0));

    private static NativeReference Prompt(string text)
    {
        var prompt = new NativeReference(Native.FMComposedPromptInitialize());
        Native.FMComposedPromptAddText(prompt.Pointer, text);
        return prompt;
    }

    private static async Task<NativeReply> RespondAsync(NativeReference session, string text, string options = Options)
    {
        using NativeReference prompt = Prompt(text);
        using var operation = new NativeOperation();
        using var task = new NativeReference(Native.FMLanguageModelSessionRespond(
            session.Pointer, prompt.Pointer, options, operation.Id, NativeOperation.TextCallback));
        return await operation.WaitAsync().ConfigureAwait(false);
    }

    private static async Task<Evidence> TextAsync(NativeReference model)
    {
        using NativeReference session = Session(model, "Follow the requested format exactly.");
        NativeReply reply = await RespondAsync(session, "Reply with exactly: café 🦊").ConfigureAwait(false);
        return new(reply.Status == 0 && reply.Content.Contains("café 🦊", StringComparison.Ordinal) ? "observed" : "unexpected",
            new { reply, responding = Native.FMLanguageModelSessionIsResponding(session.Pointer) });
    }

    private static async Task<Evidence> StreamAsync(NativeReference model, bool cancel)
    {
        using NativeReference session = Session(model);
        using NativeReference prompt = Prompt(cancel ? "Write a long description of a peaceful garden, at least 500 words."
            : "Write two short sentences about a café. Include the word café.");
        using var operation = new NativeOperation(streaming: true);
        using var stream = new NativeReference(Native.FMLanguageModelSessionStreamResponse(session.Pointer, prompt.Pointer, Options));
        Native.FMLanguageModelSessionResponseStreamIterate(stream.Pointer, operation.Id, NativeOperation.TextCallback);
        double? cancellationMs = null;
        bool? completedBeforeCancellation = null;
        bool? respondingBeforeCancellation = null;
        if (cancel)
        {
            await operation.FirstSnapshot.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            respondingBeforeCancellation = Native.FMLanguageModelSessionIsResponding(session.Pointer);
            completedBeforeCancellation = !operation.RequestCancellation();
            var clock = Stopwatch.StartNew();
            stream.Dispose(); // Upstream's stream destructor requests iteration-task cancellation.
            await operation.WaitAsync().ConfigureAwait(false);
            cancellationMs = clock.Elapsed.TotalMilliseconds;
        }
        NativeReply terminal = await operation.WaitAsync().ConfigureAwait(false);
        string previous = string.Empty;
        bool appendOnly = true;
        foreach (string snapshot in operation.Snapshots)
        {
            appendOnly &= snapshot.StartsWith(previous, StringComparison.Ordinal);
            previous = snapshot;
        }
        await Task.Delay(100).ConfigureAwait(false);
        string outcome = operation.TerminalCallbacks != 1 ? "unexpected"
            : cancel && completedBeforeCancellation == true ? "inconclusive"
            : cancel && !terminal.CancellationRequested ? "unexpected"
            : !cancel && terminal.Status != 0 ? "unexpected" : "observed";
        return new(outcome,
            new
            {
                terminal,
                snapshots = operation.Snapshots.Length,
                appendOnly,
                finalText = previous,
                cancellationMs,
                completedBeforeCancellation,
                respondingBeforeCancellation,
                operation.TerminalCallbacks,
                responding = Native.FMLanguageModelSessionIsResponding(session.Pointer)
            });
    }

    private static string Transcript(NativeReference session)
    {
        nint result = Native.FMLanguageModelSessionGetTranscriptJSONString(session.Pointer, out int code, out nint error);
        string message = Native.TakeString(error);
        string json = Native.TakeString(result);
        if (code != 0 || json.Length == 0) throw new InvalidOperationException($"Transcript error {code}: {message}");
        return json;
    }

    private static async Task<Evidence> HistoryAsync(NativeReference model)
    {
        using NativeReference session = Session(model, "Remember facts provided by the user. Answer concisely.");
        NativeReply first = await RespondAsync(session, "The project codename is LANTERN. Reply only OK.").ConfigureAwait(false);
        string transcript = Transcript(session);
        nint pointer = Native.FMTranscriptCreateFromJSONString(transcript, out int code, out nint error);
        string message = Native.TakeString(error);
        if (code != 0) throw new InvalidOperationException($"Restore error {code}: {message}");
        using var restored = new NativeReference(pointer);
        bool equal = JsonNode.DeepEquals(JsonNode.Parse(transcript), JsonNode.Parse(Transcript(restored)));
        NativeReply second = await RespondAsync(restored, "What is the project codename? Reply with only the codename.").ConfigureAwait(false);
        return new(first.Status == 0 && second.Status == 0 && equal && second.Content.Contains("LANTERN", StringComparison.Ordinal)
            ? "observed" : "unexpected", new { first, roundTripEqual = equal, second, transcript = JsonNode.Parse(transcript) });
    }

    private static NativeReference Property(NativeReference schema, string name, string type, bool optional = false)
    {
        var property = new NativeReference(Native.FMGenerationSchemaPropertyCreate(name, null, type, optional));
        Native.FMGenerationSchemaAddProperty(schema.Pointer, property.Pointer);
        return property;
    }

    private static unsafe void EnumGuide(NativeReference property)
    {
        nint first = Marshal.StringToCoTaskMemUTF8("red");
        nint second = Marshal.StringToCoTaskMemUTF8("blue");
        try
        {
            nint* choices = stackalloc nint[2] { first, second };
            Native.FMGenerationSchemaPropertyAddAnyOfGuide(property.Pointer, (nint)choices, 2, false);
        }
        finally { Marshal.FreeCoTaskMem(first); Marshal.FreeCoTaskMem(second); }
    }

    private static async Task<Evidence> SchemaAsync(NativeReference model, bool nested)
    {
        using NativeReference session = Session(model);
        using var schema = new NativeReference(Native.FMGenerationSchemaCreate("ProbeResult", "Synthetic structured result"));
        using var child = new NativeReference(Native.FMGenerationSchemaCreate("Child", "Nested synthetic value"));
        using NativeReference childName = Property(child, "name", "string");
        Native.FMGenerationSchemaAddReferenceSchema(schema.Pointer, child.Pointer);
        using NativeReference label = Property(schema, "label", "string");
        using NativeReference count = Property(schema, "count", "integer");
        Native.FMGenerationSchemaPropertyAddRangeGuide(count.Pointer, 1, 5, false);
        using NativeReference enabled = Property(schema, "enabled", "boolean");
        using NativeReference number = Property(schema, "ratio", "number");
        using NativeReference color = Property(schema, "color", "string");
        EnumGuide(color);
        using NativeReference values = Property(schema, "tags", "array<string>");
        Native.FMGenerationSchemaPropertyAddCountGuide(values.Pointer, 2, false);
        using NativeReference optional = Property(schema, "note", "string", true);
        using NativeReference? item = nested ? Property(schema, "child", "Child") : null;
        using NativeReference? children = nested ? Property(schema, "children", "array<Child>") : null;
        string schemaJson = Native.TakeString(Native.FMGenerationSchemaGetJSONString(schema.Pointer, out int code, out nint error));
        string message = Native.TakeString(error);
        if (code != 0) return new("limitation", new { code, message });
        using NativeReference prompt = Prompt("Set label to apple, count to 3, enabled to true, ratio to 1.5, color to red, tags to fresh and local. "
            + "No note was provided. If a child is requested, name it Ada. If children are requested, include one named Ben.");
        using var operation = new NativeOperation();
        using var task = new NativeReference(Native.FMLanguageModelSessionRespondWithSchema(
            session.Pointer, prompt.Pointer, schema.Pointer, Options, operation.Id, NativeOperation.StructuredCallback));
        NativeReply reply = await operation.WaitAsync().ConfigureAwait(false);
        bool valid = false;
        if (reply.Status == 0)
        {
            using JsonDocument document = JsonDocument.Parse(reply.Content);
            JsonElement root = document.RootElement;
            valid = root.GetProperty("label").ValueKind == JsonValueKind.String
                && root.GetProperty("count").TryGetInt32(out int value) && value is >= 1 and <= 5
                && root.GetProperty("enabled").ValueKind is JsonValueKind.True or JsonValueKind.False
                && root.GetProperty("ratio").ValueKind == JsonValueKind.Number
                && root.GetProperty("color").GetString() is "red" or "blue"
                && root.GetProperty("tags").GetArrayLength() == 2
                && (!nested || root.GetProperty("child").GetProperty("name").ValueKind == JsonValueKind.String
                    && root.GetProperty("children")[0].GetProperty("name").ValueKind == JsonValueKind.String);
        }
        return new(valid ? "observed" : "unexpected", new { reply, validatedShape = valid, appleSchema = JsonNode.Parse(schemaJson) });
    }

    private static async Task<Evidence> StandardSchemaAsync(NativeReference model)
    {
        using NativeReference session = Session(model);
        using NativeReference prompt = Prompt("Return label apple.");
        using var operation = new NativeOperation();
        const string json = "{\"type\":\"object\",\"properties\":{\"label\":{\"type\":\"string\"}},\"required\":[\"label\"],\"additionalProperties\":false}";
        using var task = new NativeReference(Native.FMLanguageModelSessionRespondWithSchemaFromJSON(
            session.Pointer, prompt.Pointer, json, Options, operation.Id, NativeOperation.StructuredCallback));
        NativeReply reply = await operation.WaitAsync().ConfigureAwait(false);
        return new(reply.Status == 0 ? "observed" : "limitation", new { standardJsonSchemaAccepted = reply.Status == 0, reply });
    }

    private static async Task<Evidence> TokenLimitAsync(NativeReference model)
    {
        using NativeReference session = Session(model);
        NativeReply reply = await RespondAsync(session, "Write a detailed paragraph describing a garden in at least 100 words.",
            "{\"temperature\":0,\"maximum_response_tokens\":8}").ConfigureAwait(false);
        using NativeReference prompt = Prompt(reply.Content);
        using var operation = new NativeOperation();
        using var task = new NativeReference(Native.FMSystemLanguageModelTokenCountForPrompt(
            model.Pointer, prompt.Pointer, operation.Id, NativeOperation.TokenCallback));
        NativeReply count = await operation.WaitAsync().ConfigureAwait(false);
        return new("observed", new
        {
            requestedCap = 8,
            reply,
            reencodedTextTokenCount = count,
            note = "Reencoded prompt token count is not native generation usage; the callback exports no finish reason."
        });
    }

    private static async Task<Evidence> CancelBeforeAsync(NativeReference model)
    {
        using NativeReference session = Session(model);
        using NativeReference prompt = Prompt("Write a long paragraph about a garden.");
        using var operation = new NativeOperation();
        using var task = new NativeReference(Native.FMLanguageModelSessionRespond(
            session.Pointer, prompt.Pointer, Options, operation.Id, NativeOperation.TextCallback));
        var clock = Stopwatch.StartNew();
        operation.RequestCancellation();
        Native.FMTaskCancel(task.Pointer);
        NativeReply reply = await operation.WaitAsync().ConfigureAwait(false);
        return new(reply.Status != 0 ? "observed" : "unexpected", new
        {
            reply,
            milliseconds = clock.Elapsed.TotalMilliseconds,
            operation.TerminalCallbacks,
            responding = Native.FMLanguageModelSessionIsResponding(session.Pointer)
        });
    }

    private static nint _activeTool;
    private static string _toolMode = string.Empty;
    private static string _toolArguments = string.Empty;
    private static int _toolCallbacks;
    private static readonly TaskCompletionSource<uint> ToolCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource ToolFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static unsafe nint ToolCallback => (nint)(delegate* unmanaged[Cdecl]<nint, uint, void>)&OnTool;

    private static unsafe NativeReference ToolSession(NativeReference model, NativeReference tool)
    {
        nint pointer = tool.Pointer;
        return new(Native.FMLanguageModelSessionCreateFromSystemLanguageModel(model.Pointer,
            "Use lookup_code to get the requested code. You cannot know the code without calling the tool. Return its result exactly.",
            (nint)(&pointer), 1));
    }

    private static unsafe NativeReference RestoreToolSession(NativeReference transcript, NativeReference model, NativeReference tool)
    {
        nint pointer = tool.Pointer;
        return new(Native.FMLanguageModelSessionCreateFromTranscript(transcript.Pointer, model.Pointer, (nint)(&pointer), 1));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [SuppressMessage("Design", "CA1031", Justification = "Managed exceptions must not cross a native callback boundary.")]
    private static void OnTool(nint arguments, uint callId)
    {
        try
        {
            string json = Native.TakeString(Native.FMGeneratedContentGetJSONString(arguments));
            _toolArguments = json;
            Interlocked.Increment(ref _toolCallbacks);
            Console.WriteLine(JsonSerializer.Serialize(new { progress = "tool-called", callId, arguments = json }));
            if (_toolMode is "tool-immediate" or "tool-duplicate")
            {
                Native.FMBridgedToolFinishCall(_activeTool, callId, "ORCHID-729");
                if (_toolMode == "tool-duplicate") Native.FMBridgedToolFinishCall(_activeTool, callId, "WRONG-DUPLICATE");
                ToolFinished.TrySetResult();
            }
            else if (_toolMode == "tool-roundtrip-replay")
            {
                // A second invocation must settle, but must not be mistaken for consuming the supplied history result.
                Native.FMBridgedToolFinishCall(_activeTool, callId, "UNEXPECTED-REEXECUTION");
            }
            ToolCalled.TrySetResult(callId);
        }
        catch (Exception error) { ToolCalled.TrySetException(error); }
        finally { Native.FMRelease(arguments); }
    }

    private static async Task<Evidence> ToolAsync(NativeReference model, string mode)
    {
        _toolMode = mode;
        using var schema = new NativeReference(Native.FMGenerationSchemaCreate("LookupArguments", "Lookup request"));
        using NativeReference key = Property(schema, "key", "string");
        nint pointer = Native.FMBridgedToolCreate("lookup_code", "Returns the secret synthetic code for a key.", schema.Pointer,
            ToolCallback, out int code, out nint error);
        string message = Native.TakeString(error);
        if (code != 0) throw new InvalidOperationException($"Tool creation error {code}: {message}");
        using var tool = new NativeReference(pointer);
        _activeTool = tool.Pointer;
        using NativeReference session = ToolSession(model, tool);
        using NativeReference prompt = Prompt("Look up the code for key sample using lookup_code, and return the code.");
        using var operation = new NativeOperation();
        using var task = new NativeReference(Native.FMLanguageModelSessionRespond(
            session.Pointer, prompt.Pointer, Options, operation.Id, NativeOperation.TextCallback));
        uint callId = await ToolCalled.Task.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (mode == "tool-roundtrip")
            return await ToolRoundTripAsync(model, tool, session, task, operation, callId).ConfigureAwait(false);
        bool completedBeforeRecovery = false;
        var completionClock = Stopwatch.StartNew();
        if (mode == "tool-delayed")
        {
            // A diagnostic control, not a proposed fix for the continuation race.
            await Task.Delay(100).ConfigureAwait(false);
            Native.FMBridgedToolFinishCall(tool.Pointer, callId, "ORCHID-729");
        }
        else if (mode is "tool-immediate" or "tool-duplicate")
        {
            await ToolFinished.Task.ConfigureAwait(false);
            completedBeforeRecovery = await CompletesWithinAsync(operation.Completion, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!completedBeforeRecovery)
            {
                Console.WriteLine("{\"progress\":\"retrying-same-tool-result-after-stall\"}");
                Native.FMBridgedToolFinishCall(tool.Pointer, callId, "ORCHID-729");
            }
        }
        else
        {
            await Task.Delay(100).ConfigureAwait(false);
            operation.RequestCancellation();
            completionClock.Restart();
            Native.FMTaskCancel(task.Pointer);
            completedBeforeRecovery = await CompletesWithinAsync(operation.Completion, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (!completedBeforeRecovery)
            {
                Console.WriteLine("{\"progress\":\"releasing-tool-result-after-cancellation-stall\"}");
                Native.FMBridgedToolFinishCall(tool.Pointer, callId, "ORCHID-729");
            }
        }
        NativeReply reply = await operation.WaitAsync().ConfigureAwait(false);
        double milliseconds = completionClock.Elapsed.TotalMilliseconds;
        if (mode == "tool-cancel-late" && completedBeforeRecovery)
        {
            Native.FMBridgedToolFinishCall(tool.Pointer, callId, "LATE-RESULT");
            Native.FMBridgedToolFinishCall(tool.Pointer, callId, "DUPLICATE-LATE-RESULT");
            await Task.Delay(100).ConfigureAwait(false);
        }
        bool limitation = mode != "tool-delayed" && !completedBeforeRecovery;
        bool expected = mode.StartsWith("tool-cancel", StringComparison.Ordinal)
            ? reply.CancellationRequested && reply.Status != 0
            : reply.Status == 0 && reply.Content.Contains("ORCHID-729", StringComparison.Ordinal)
                && !reply.Content.Contains("WRONG-DUPLICATE", StringComparison.Ordinal);
        return new(limitation ? "limitation" : expected && operation.TerminalCallbacks == 1 ? "observed" : "unexpected",
            new
            {
                mode,
                completedBeforeRecovery,
                milliseconds,
                reply,
                operation.TerminalCallbacks,
                responding = Native.FMLanguageModelSessionIsResponding(session.Pointer),
                transcript = JsonNode.Parse(Transcript(session))
            });
    }

    private static async Task<Evidence> ToolRoundTripAsync(NativeReference model, NativeReference tool,
        NativeReference session, NativeReference task, NativeOperation operation, uint bridgeCallId)
    {
        JsonNode pending;
        string callbackArguments = _toolArguments;
        try { pending = JsonNode.Parse(Transcript(session))!; }
        finally
        {
            operation.RequestCancellation();
            Native.FMTaskCancel(task.Pointer);
        }
        NativeReply cancellation = await operation.WaitAsync().ConfigureAwait(false);
        JsonNode afterCancellation = JsonNode.Parse(Transcript(session))!;
        bool idle = !Native.FMLanguageModelSessionIsResponding(session.Pointer);
        task.Dispose();
        session.Dispose(); // The second request cannot depend on this live native session.

        JsonArray pendingEntries = pending["transcript"]!["entries"]!.AsArray();
        JsonNode[] calls = pendingEntries.SelectMany(static entry =>
            entry?["toolCalls"]?.AsArray().OfType<JsonNode>() ?? []).ToArray();
        if (calls.Length != 1)
            return new("limitation", new
            {
                reason = "Pending transcript did not expose exactly one native tool call.",
                bridgeCallId,
                callbackArguments,
                pending,
                afterCancellation,
                cancellation,
                idle
            });

        string nativeCallId = calls[0]["id"]!.GetValue<string>();
        bool argumentsMatch = JsonNode.DeepEquals(JsonNode.Parse(callbackArguments),
            JsonNode.Parse(calls[0]["arguments"]!.GetValue<string>()));
        // Research-only wire fixture based on the bridge's own exported tool-output entry.
        // Preserve all existing entries; append the host's synthetic result with the native call ID.
        JsonNode replay = pending.DeepClone();
        JsonArray replayEntries = replay["transcript"]!["entries"]!.AsArray();
        replayEntries.Add(JsonSerializer.SerializeToNode(new
        {
            id = nativeCallId,
            toolCallID = nativeCallId,
            toolName = "lookup_code",
            role = "tool",
            contents = new[] { new { id = Guid.NewGuid().ToString(), type = "text", text = "ORCHID-729" } }
        }));
        nint pointer = Native.FMTranscriptCreateFromJSONString(replay.ToJsonString(), out int code, out nint error);
        string message = Native.TakeString(error);
        if (code != 0)
            return new("limitation", new
            {
                reason = "The transcript plus host result was rejected.",
                code,
                message,
                bridgeCallId,
                nativeCallId,
                argumentsMatch,
                pending,
                afterCancellation,
                cancellation,
                idle,
                replay
            });
        using var transcript = new NativeReference(pointer);
        using NativeReference restored = RestoreToolSession(transcript, model, tool);
        bool restoredExactly = JsonNode.DeepEquals(replay, JsonNode.Parse(Transcript(restored)));
        _toolMode = "tool-roundtrip-replay";
        using var emptyPrompt = new NativeReference(Native.FMComposedPromptInitialize());
        using var resumed = new NativeOperation();
        using var resumedTask = new NativeReference(Native.FMLanguageModelSessionRespond(
            restored.Pointer, emptyPrompt.Pointer, Options, resumed.Id, NativeOperation.TextCallback));
        NativeReply reply = await resumed.WaitAsync().ConfigureAwait(false);
        JsonNode afterReplay = JsonNode.Parse(Transcript(restored))!;
        JsonArray finalEntries = afterReplay["transcript"]!["entries"]!.AsArray();
        bool prefixPreserved = finalEntries.Count >= replayEntries.Count
            && replayEntries.Select((entry, index) => JsonNode.DeepEquals(entry, finalEntries[index])).All(static equal => equal);
        int addedUserEntries = finalEntries.Skip(replayEntries.Count).Count(static entry => entry?["role"]?.GetValue<string>() == "user");
        bool consumedResult = reply.Status == 0 && reply.Content.Contains("ORCHID-729", StringComparison.Ordinal)
            && !reply.Content.Contains("UNEXPECTED-REEXECUTION", StringComparison.Ordinal);
        bool continuedFaithfully = argumentsMatch && restoredExactly && prefixPreserved && addedUserEntries == 0
            && consumedResult && _toolCallbacks == 1;
        return new(!idle || operation.TerminalCallbacks != 1 || resumed.TerminalCallbacks != 1 ? "unexpected"
            : continuedFaithfully ? "observed" : "limitation", new
            {
                bridgeCallId,
                nativeCallId,
                callbackArguments,
                argumentsMatch,
                pending,
                afterCancellation,
                cancellation,
                idle,
                restoredExactly,
                prefixPreserved,
                addedUserEntries,
                consumedResult,
                toolCallbacks = _toolCallbacks,
                reply,
                afterReplay,
                continuedFaithfully,
                originalTerminalCallbacks = operation.TerminalCallbacks,
                resumedTerminalCallbacks = resumed.TerminalCallbacks,
                responding = Native.FMLanguageModelSessionIsResponding(restored.Pointer)
            });
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        try { await task.WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }
}
