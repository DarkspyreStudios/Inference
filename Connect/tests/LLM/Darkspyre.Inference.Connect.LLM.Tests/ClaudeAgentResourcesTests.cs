using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed partial class ClaudeAgentResourcesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("connect-claude-resources-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("result.json", false)]
    public async Task NativeOpeningCarriesTheActualLogicalFilesWithoutTakingSelectionOwnership(string fileName, bool generatedByTool)
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var generated = new InferenceResourceReference("workspace", "generated");
        byte[] bytes = fileName.EndsWith(".pdf", StringComparison.Ordinal)
            ? "%PDF-1.7\nactual generated content"u8.ToArray() : "{\"answer\":42}"u8.ToArray();
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 4, [generated, reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray()), (generated, fileName, bytes)]);
        var context = new LlmAgentContext
        {
            Revision = 9,
            ResourceSelectionVersion = new("workspace", "session", 4),
            SelectedResources = [generated, reference],
            Entries = [new("prompt", "old-turn", new("user", "Read the reference")
                { Attachments = [new LlmResourceAttachment(reference), new LlmResourceAttachment(reference)] }),
                new("answer", "old-turn", new("assistant", "Created " + fileName))]
        };
        if (generatedByTool)
            context = context with
            {
                Entries = [context.Entries[0], context.Entries[1] with
                { Message = context.Entries[1].Message with { ToolCalls = [new LlmToolCall("create-1", "create_file", "{}")] } },
                    new("tool", "old-turn", new("user", string.Empty)
                    { ToolResults = [new LlmToolResult("create-1", new() { Parts = [new LlmToolOutputSessionResource(generated)] })] })]
            };
        LlmAgentContext restored = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(context, ConnectLlmJson.AgentContext),
            ConnectLlmJson.AgentContext)!;
        var process = new ManualProcess();
        var connections = new ResourcePreparationTests.Source(Connection());
        await using var catalogs = new LlmProviderService(connections, [], []);
        await using var providers = new LlmAgentProviderService(connections, [Client(new Factory(process))], catalogs);
        await using ILlmAgentSession session = await providers.StartSessionAsync("connection", new()
        {
            ModelId = "claude-test",
            Context = restored,
            CallerServices = new() { Resources = access },
            Runtime = new() { WorkingDirectory = _directory }
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(
            new() { Input = [new LlmAgentTextContent("Compare the files")] }, limit.Token).GetAsyncEnumerator(limit.Token);
        Assert.True(await feed.MoveNextAsync());
        Assert.IsType<LlmAgentTurnStartedEvent>(feed.Current);
        string turnId = await process.WaitForUserMessage(limit.Token);
        JsonElement input = Assert.Single(process.Writes(), item => item.GetProperty("type").GetString() == "user");
        JsonElement[] documents = input.GetProperty("message").GetProperty("content").EnumerateArray()
            .Where(part => part.GetProperty("type").GetString() == "document").ToArray();
        Assert.Equal(["reference.txt", "reference.txt", fileName], documents.Select(part => part.GetProperty("title").GetString()));
        JsonElement source = documents[^1].GetProperty("source");
        Assert.Equal(bytes, source.GetProperty("type").GetString() == "base64"
            ? Convert.FromBase64String(source.GetProperty("data").GetString()!) : Encoding.UTF8.GetBytes(source.GetProperty("data").GetString()!));
        Assert.Equal(2, access.Reads.Count);
        Assert.All(access.Reads, stream => Assert.False(stream.CanRead));
        Assert.Equal(2, access.ReleaseAttempts);
        process.Push(new { type = "user", uuid = turnId, isReplay = true });
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "answer", stop_reason = "end_turn" });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.CaptureContextAsync());
        Assert.Equal([generated, reference], access.Selection.SelectedResources);
        Assert.Equal(4, access.Selection.Revision);
        Assert.Equal(9, restored.Revision);
        Assert.IsType<LlmResourceAttachment>(restored.Entries[0].Message.Attachments[0]);
    }

    [Fact]
    public async Task NativeMcpMaterializesOnlyTheToolsNamedSelectedResourceAndKeepsItsLogicalResult()
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var generated = new InferenceResourceReference("workspace", "generated");
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 4, [generated, reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray()), (generated, "result.json", "{\"answer\":42}"u8.ToArray())]);
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            CallerServices = new() { Resources = access },
            Tools = [new LlmAgentToolDefinition { Name = "hold", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = (_, _) => Task.FromResult(new LlmToolOutput { Parts = [new LlmToolOutputSessionResource(generated)] })
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, process, limit.Token);
        // The turn's input carried the selection; the tool's result reads only the resource it names.
        int reads = access.Reads.Count;
        int releases = access.ReleaseAttempts;
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 7));
        JsonElement answer = await AnswerFor(process, "c1");
        JsonElement resource = Assert.Single(answer.GetProperty("result").GetProperty("content").EnumerateArray()).GetProperty("resource");
        Assert.Equal("application/json", resource.GetProperty("mimeType").GetString());
        Assert.Equal("{\"answer\":42}", resource.GetProperty("text").GetString());
        Assert.Equal(reads + 1, access.Reads.Count);
        Assert.False(access.Reads[^1].CanRead);
        Assert.Equal(releases + 1, access.ReleaseAttempts);
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "answer", stop_reason = "end_turn" });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);
        LlmAgentToolEvent tool = Assert.Single(events.OfType<LlmAgentToolEvent>(), item => item.Output is not null);
        Assert.Equal(generated, Assert.IsType<LlmToolOutputSessionResource>(Assert.Single(tool.Output!.Parts)).Resource);
        Assert.Equal([generated, reference], access.Selection.SelectedResources);
    }

    [Fact]
    public async Task UpdatePlanRunsInsideConnectOnClaude()
    {
        var process = new ManualProcess();
        int hostCalls = 0;
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            ToolHandler = (_, _) => { Interlocked.Increment(ref hostCalls); return Task.FromResult(LlmToolOutput.FromText("host")); }
        });

        process.Push(ToolUse("tu1", "update_plan"));
        process.Push(ToolCall("c1", 1, "update_plan", new { plan = new[] { new { step = "look", status = "in_progress" } } }));
        JsonElement answer = await AnswerFor(process, "c1");
        Assert.Equal("Plan updated.", answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(0, hostCalls);
    }

    [Fact]
    public async Task SequentialToolsRunOneAtATime()
    {
        var process = new ManualProcess();
        int running = 0;
        int overlapped = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            Tools = [new LlmAgentToolDefinition { Name = "hold", InputSchema = Json(new { type = "object" }), RequiresSequentialExecution = true }],
            ToolHandler = async (_, token) =>
            {
                if (Interlocked.Increment(ref running) > 1) Interlocked.Increment(ref overlapped);
                if (Interlocked.Increment(ref calls) == 1) { first.TrySetResult(); await release.Task.WaitAsync(token); }
                Interlocked.Decrement(ref running);
                return LlmToolOutput.FromText("done");
            }
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 1));
        await first.Task.WaitAsync(limit.Token);
        process.Push(ToolCall("c2", 2));
        await Task.Delay(100, limit.Token);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.TrySetResult();
        await AnswerFor(process, "c1");
        await AnswerFor(process, "c2");
        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(0, overlapped);
    }

    [Fact]
    public async Task CompactNowReopensFromTheCompactedHistory()
    {
        var first = new ManualProcess();
        var second = new ManualProcess();
        var factory = new Factory(first, second);
        await using ILlmAgentSession session = await Client(factory).StartSessionAsync("connection", Connection(),
            Options(History(("t1", Words("alpha", 2000), "first answer"), ("t2", "second question", "second answer")), Window(4000)));

        LlmAgentCompactedEvent? compacted = await session.CompactNowAsync();

        Assert.NotNull(compacted);
        Assert.Equal(2, compacted.DroppedMessageCount);
        Assert.Equal("t2", compacted.TurnId);
        // The host keeps its own copy of a Claude conversation and replaces it with this context.
        LlmAgentContext carried = Assert.IsType<LlmAgentContext>(compacted.Context);
        Assert.Equal(compacted.ContextRevision, carried.Revision);
        Assert.Equal([ConversationCompactor.SummaryTurnId, "t2", "t2"], carried.Entries.Select(entry => entry.TurnId));
        Assert.Equal(1, first.Disposals);
        Assert.Equal(2, factory.Invocations.Count);
        await CompleteTurnAsync(session, second);
        string sent = UserText(second);
        Assert.Contains("summary of the earlier work", sent, StringComparison.Ordinal);
        Assert.Contains("second question", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha alpha", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextStatusReportsUsedAndTotal()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Client(new Factory(process)).StartSessionAsync("connection", Connection(),
            Options(History(("t1", "first question", "first answer")), null) with { ModelId = "claude-sonnet-4-5" });

        LlmAgentContextStatus status = await session.GetContextStatusAsync();
        LlmAgentContextStatus prospective = await session.GetContextStatusAsync(new() { Input = [new LlmAgentTextContent(Words("gamma", 100))] });

        Assert.Equal(200_000, status.MaxContextTokens);
        Assert.True(status.UsedContextTokens > 0);
        Assert.False(status.NeedsCompaction);
        Assert.Equal(LlmContextMeasurementSource.Estimated, status.Source);
        Assert.True(prospective.UsedContextTokens >= status.UsedContextTokens + 100);
    }

    [Fact]
    public async Task ASteerDuringAToolCallJoinsTheSameTurn()
    {
        var process = new ManualProcess();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ILlmAgentSession session = await StartWithTool(process, async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return LlmToolOutput.FromText("held");
        });
        Assert.Equal(InferenceSupport.Supported, (await session.GetCapabilitiesAsync()).SupportsSteering);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, process, limit.Token);
        List<LlmAgentEvent> events = [feed.Current];
        string turnId = feed.Current.TurnId!;
        string hostInput = await process.WaitForUserMessage(limit.Token);
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 1));
        await entered.Task.WaitAsync(limit.Token);

        await session.SteerAsync(turnId, [new LlmAgentTextContent("Also end with BANANA.")], [], limit.Token);

        string steerId = await process.WaitForUserMessage(limit.Token, index: 1);
        JsonElement steer = process.Writes().Single(root => root.GetProperty("type").GetString() == "user"
            && root.GetProperty("uuid").GetString() == steerId);
        Assert.Equal("next", steer.GetProperty("priority").GetString());
        Assert.Equal("human", steer.GetProperty("origin").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, steer.GetProperty("parent_tool_use_id").ValueKind);
        Assert.Equal("Also end with BANANA.", steer.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
        // The CLI folds the steer into the running turn once the tool result arrives: its echo starts no host turn.
        process.Push(Lifecycle(steerId, "queued"));
        release.TrySetResult();
        await AnswerFor(process, "c1");
        process.Push(new { type = "user", uuid = steerId, isReplay = true, origin = new { kind = "human" } });
        process.Push(Lifecycle(steerId, "started"));
        process.Push(new { type = "assistant", message = new { id = "m2", content = new[] { new { type = "text", text = "DONE BANANA" } } } });
        process.Push(Lifecycle(steerId, "completed"));
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "DONE BANANA", stop_reason = "end_turn", queued_turn_count = 0 });
        process.Push(Lifecycle(hostInput, "completed"));
        events.AddRange(await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent));

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Equal(["DONE BANANA"], events.OfType<LlmAgentMessageCompletedEvent>().Select(item => item.Text));
        Assert.All(events, item => Assert.Equal(turnId, item.TurnId));
        Assert.Empty(events.OfType<LlmAgentWarningEvent>());
    }

    private static object Lifecycle(string commandId, string state) => new { type = "command_lifecycle", command_uuid = commandId, state };

    private static object Result(string text, int queued, int input, int output) => new
    {
        type = "result",
        subtype = "success",
        is_error = false,
        result = text,
        stop_reason = "end_turn",
        queued_turn_count = queued,
        usage = new { input_tokens = input, output_tokens = output }
    };

    private LlmAgentSessionOptions Options(LlmAgentContext? context, LlmAgentExecutionOptions? execution) => new()
    {
        ModelId = "claude-test",
        Runtime = new() { WorkingDirectory = _directory },
        Context = context,
        ExecutionOptions = execution
    };

    /// <summary>A window of <paramref name="tokens"/> with a 100-token output reserve.</summary>
    private static LlmAgentExecutionOptions Window(long tokens) =>
        new() { ContextPolicy = new() { MaxContextTokens = tokens, ReservedOutputTokens = 100 } };

    private static LlmAgentContext History(params (string TurnId, string User, string Assistant)[] turns) => new()
    {
        Entries = [.. turns.SelectMany(turn => new LlmAgentContextEntry[]
        {
            new($"{turn.TurnId}-user", turn.TurnId, new("user", turn.User)),
            new($"{turn.TurnId}-assistant", turn.TurnId, new("assistant", turn.Assistant))
        })]
    };

    private static string Words(string word, int count) => string.Concat(Enumerable.Repeat(word + " ", count));

    /// <summary>The text the CLI received in its first user message.</summary>
    private static string UserText(ManualProcess process) => string.Join("\n", process.Writes()
        .First(item => item.GetProperty("type").GetString() == "user").GetProperty("message").GetProperty("content")
        .EnumerateArray().Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()));

    /// <summary>Runs one turn the CLI answers with "done".</summary>
    private static async Task<List<LlmAgentEvent>> CompleteTurnAsync(ILlmAgentSession session, ManualProcess process, int index = 0)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<LlmAgentEvent> feed = await HostTurnAsync(session, process, limit.Token, index: index);
        List<LlmAgentEvent> events = [feed.Current];
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "done", stop_reason = "end_turn" });
        events.AddRange(await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent));
        return events;
    }

    /// <summary>Starts a host turn and echoes its input, so the CLI output that follows belongs to it.</summary>
    private static async Task<IAsyncEnumerator<LlmAgentEvent>> HostTurnAsync(ILlmAgentSession session, ManualProcess process,
        CancellationToken token, string input = "go", int index = 0)
    {
        IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(new() { Input = [new LlmAgentTextContent(input)] }, token)
            .GetAsyncEnumerator(token);
        Assert.True(await feed.MoveNextAsync());
        Assert.IsType<LlmAgentTurnStartedEvent>(feed.Current);
        string turnId = await process.WaitForUserMessage(token, index);
        process.Push(new { type = "user", uuid = turnId, isReplay = true });
        return feed;
    }

    private Task<ILlmAgentSession> Start(ManualProcess process, LlmConnection? connection = null) =>
        Client(new Factory(process)).StartSessionAsync("connection", connection ?? Connection(), new() { ModelId = "claude-test", Runtime = new() { WorkingDirectory = _directory } });

    private Task<ILlmAgentSession> StartWithTool(ManualProcess process, LlmAgentToolHandler handler) =>
        Client(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            Tools = [new LlmAgentToolDefinition { Name = "hold", Description = "Holds.", InputSchema = Json(new { type = "object" }) }],
            ToolHandler = handler,
        });

    private static object ToolUse(string id, string name = "hold") => new
    {
        type = "assistant",
        message = new { id = "m-" + id, content = new object[] { new { type = "tool_use", id, name = "mcp__darkspyre-inference-connect__" + name, input = new { } } } },
    };

    private static object ToolCall(string requestId, object id, string name = "hold", object? arguments = null) => new
    {
        type = "control_request",
        request_id = requestId,
        request = new { subtype = "mcp_message", server_name = "darkspyre-inference-connect", message = new { method = "tools/call", @params = new { name, arguments = arguments ?? new { } }, jsonrpc = "2.0", id } },
    };

    /// <summary>The MCP message Connect answered a control request with.</summary>
    private static async Task<JsonElement> AnswerFor(ManualProcess process, string requestId)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (process.Writes().FirstOrDefault(root => root.GetProperty("type").GetString() == "control_response"
                && root.GetProperty("response").GetProperty("request_id").GetString() == requestId) is { ValueKind: JsonValueKind.Object } answer)
            {
                return answer.GetProperty("response").GetProperty("response").GetProperty("mcp_response");
            }
            await Task.Delay(10, limit.Token);
        }
    }

    private static LlmConnection Connection() => new()
    {
        Version = "v1", Transport = LlmTransport.AnthropicClaudeCli, Endpoint = "claude",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "account-token" } }.Serialize()
    };

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static async Task<List<LlmAgentEvent>> ReadUntil(IAsyncEnumerator<LlmAgentEvent> feed, Func<LlmAgentEvent, bool> stop)
    {
        var events = new List<LlmAgentEvent>();
        while (await feed.MoveNextAsync())
        {
            events.Add(feed.Current);
            if (stop(feed.Current)) { return events; }
        }
        return events;
    }

    private static readonly HttpClient Http = new();

    private static ClaudeAgentClient Client(IClaudeCliSessionProcessFactory factory, Summaries? summaries = null) =>
        new(factory, new ClaudeCliLlmClient(Http, summaries ?? new Summaries(), NoRateLimitLog), new LearnedModelContext(),
            NoRateLimitLog, NullLogger<ClaudeAgentClient>.Instance);

    private static readonly ClaudeRateLimitLog NoRateLimitLog = new(NullLogger<ClaudeRateLimitLog>.Instance);

    /// <summary>Starts the given processes in order; the last one serves every later start.</summary>
    private sealed class Factory(params IClaudeCliSessionProcess[] processes) : IClaudeCliSessionProcessFactory
    {
        private readonly List<ClaudeCliSessionInvocation> _invocations = [];

        public ClaudeCliSessionInvocation? Invocation { get { lock (_invocations) return _invocations.Count == 0 ? null : _invocations[^1]; } }

        public IReadOnlyList<ClaudeCliSessionInvocation> Invocations { get { lock (_invocations) return [.. _invocations]; } }

        public Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default)
        {
            int index;
            lock (_invocations)
            {
                index = _invocations.Count;
                _invocations.Add(invocation);
            }
            return Task.FromResult(processes[Math.Min(index, processes.Length - 1)]);
        }
    }

    /// <summary>The print-mode CLI behind summary side requests: every request returns the same summary.</summary>
    private sealed class Summaries(string text = "summary of the earlier work") : IClaudeCliRunner
    {
        public ConcurrentQueue<ClaudeCliInvocation> Invocations { get; } = new();

        public Task<ClaudeCliProcessResult> RunAsync(ClaudeCliInvocation invocation, CancellationToken cancellationToken = default)
        {
            Invocations.Enqueue(invocation);
            return Task.FromResult(new ClaudeCliProcessResult(0, JsonSerializer.Serialize(new
            {
                type = "result",
                subtype = "success",
                is_error = false,
                result = text,
                stop_reason = "end_turn",
                usage = new { input_tokens = 7, output_tokens = 3 }
            }), string.Empty));
        }
    }

    /// <summary>A CLI whose output the test pushes. Control requests other than interrupt are answered with success.</summary>
    private sealed class ManualProcess : IClaudeCliSessionProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly ConcurrentQueue<string> _writes = new();

        public JsonElement? InitializationPayload { get; init; }
        public int Disposals { get; private set; }

        public void Push(object value) => _output.Writer.TryWrite(JsonSerializer.Serialize(value));

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            _writes.Enqueue(line);
            if (root.GetProperty("type").GetString() == "control_request"
                && root.GetProperty("request").GetProperty("subtype").GetString() != "interrupt")
            {
                JsonElement payload = root.GetProperty("request").GetProperty("subtype").GetString() == "initialize"
                    && InitializationPayload is { } initialization ? initialization : JsonElement.Parse("{}");
                _output.Writer.TryWrite(new JsonObject
                {
                    ["type"] = "control_response",
                    ["response"] = new JsonObject
                    {
                        ["subtype"] = "success",
                        ["request_id"] = root.GetProperty("request_id").GetString(),
                        ["response"] = JsonNode.Parse(payload.GetRawText())
                    }
                }.ToJsonString());
            }
            return ValueTask.CompletedTask;
        }

        public IEnumerable<JsonElement> Writes() => _writes.Select(line => JsonSerializer.Deserialize<JsonElement>(line));

        public IEnumerable<JsonElement> Requests() => _writes
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(root => root.GetProperty("type").GetString() == "control_request")
            .Select(root => root.GetProperty("request"));

        public async Task<string> WaitForUserMessage(CancellationToken cancellationToken, int index = 0)
        {
            while (true)
            {
                if (_writes.Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                    .Where(root => root.GetProperty("type").GetString() == "user").Skip(index)
                    .FirstOrDefault() is { ValueKind: JsonValueKind.Object } user)
                {
                    return user.GetProperty("uuid").GetString()!;
                }
                await Task.Delay(10, cancellationToken);
            }
        }

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _output.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public Task<ClaudeCliProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClaudeCliProcessResult(0, string.Empty, string.Empty));

        public ValueTask DisposeAsync()
        {
            Disposals++;
            _output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
