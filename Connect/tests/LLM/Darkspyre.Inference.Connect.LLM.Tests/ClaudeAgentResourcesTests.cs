using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ClaudeAgentResourcesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("connect-claude-resources-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ATrailerWrittenAfterTheTaskEndedIsStillExcludedAndGivesTheExitCode()
    {
        var events = new ConcurrentQueue<LlmAgentEvent>();
        await using var tasks = new ClaudeBackgroundTasks("thread", 1_048_576, events.Enqueue, TimeSpan.FromMilliseconds(20));
        string killed = Path.Combine(_directory, "b1.output");
        string exited = Path.Combine(_directory, "b2.output");
        await File.WriteAllTextAsync(killed, "tick-1\n");
        await File.WriteAllTextAsync(exited, "done\n");
        foreach ((string id, string path) in new[] { ("b1", killed), ("b2", exited) })
        {
            tasks.Handle(Json(new { type = "system", subtype = "task_started", task_id = id, tool_use_id = "tu-" + id, description = id, is_backgrounded = true, task_type = "local_bash" }));
            tasks.ToolResult("tu-" + id, $"Command running in background with ID: {id}. Output is being written to: {path}. You will be notified.");
        }

        // The stop and the completion both end their tasks before the CLI appends the trailer.
        tasks.Handle(Json(new { type = "system", subtype = "task_updated", task_id = "b1", patch = new { status = "killed" } }));
        tasks.Handle(Json(new { type = "system", subtype = "task_updated", task_id = "b2", patch = new { status = "completed" } }));
        await File.AppendAllTextAsync(killed, "\n[killed]\n");
        await File.AppendAllTextAsync(exited, "\n[exited with code 2]");

        LlmAgentResourceOutput read = await tasks.ReadAsync("b1", null, CancellationToken.None);
        Assert.Equal(("tick-1\n", "7", true), (Encoding.UTF8.GetString(read.Data.Span), read.NextCursor, read.Closed));
        Assert.Equal((LlmAgentResourceOutputStatus.NoData, true), ((await tasks.ReadAsync("b1", "7", CancellationToken.None)).Status, read.Closed));
        Assert.Null(tasks.Snapshot().Single(resource => resource.ResourceId == "b2").ExitCode);
        tasks.Handle(Json(new { type = "system", subtype = "task_notification", task_id = "b2", tool_use_id = "tu-b2", status = "completed", output_file = exited, summary = "done" }));
        Assert.Equal(2, tasks.Snapshot().Single(resource => resource.ResourceId == "b2").ExitCode);
        Assert.Equal("done\n", Encoding.UTF8.GetString((await tasks.ReadAsync("b2", null, CancellationToken.None)).Data.Span));
    }

    [Fact]
    public async Task ARuntimeStartedTurnArrivesOnTheSessionFeedNamingTheTaskThatEnded()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Start(process);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.ObserveAsync(limit.Token).GetAsyncEnumerator(limit.Token);

        process.Push(new { type = "system", subtype = "task_started", task_id = "t1", tool_use_id = "tu1", description = "sleep", is_backgrounded = true, task_type = "local_bash" });
        process.Push(new { type = "system", subtype = "task_notification", task_id = "t1", tool_use_id = "tu1", status = "completed", output_file = "", summary = "done" });
        process.Push(new { type = "assistant", message = new { id = "m1", content = new object[] { new { type = "text", text = "It finished." } } } });
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "It finished." });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);

        LlmAgentProviderTurnStartedEvent started = Assert.Single(events.OfType<LlmAgentProviderTurnStartedEvent>());
        Assert.Equal("t1", started.ResourceId);
        Assert.Equal("It finished.", Assert.Single(events.OfType<LlmAgentMessageCompletedEvent>()).Text);
        Assert.Equal(started.TurnId, Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]).TurnId);
        Assert.Equal([LlmAgentResourceChange.Added, LlmAgentResourceChange.Ended],
            events.OfType<LlmAgentResourceEvent>().Select(item => item.Change));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (LlmAgentEvent _ in session.ObserveAsync()) { }
        });
    }

    [Fact]
    public async Task AHostToolCallRunsOffTheReaderAndTheCliCanCancelIt()
    {
        var process = new ManualProcess();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ILlmAgentSession session = await StartWithTool(process, async (_, token) =>
        {
            entered.TrySetResult(token);
            await Task.Delay(Timeout.Infinite, token);
            return LlmToolOutput.FromText("unreachable");
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.ObserveAsync(limit.Token).GetAsyncEnumerator(limit.Token);
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 7));
        CancellationToken token = await entered.Task.WaitAsync(limit.Token);

        // The reader keeps routing while the tool runs: a task starts, and its stop request completes.
        process.Push(new { type = "system", subtype = "task_started", task_id = "s1", tool_use_id = "tu-s1", description = "server", is_backgrounded = true, task_type = "local_bash" });
        await ReadUntil(feed, item => item is LlmAgentResourceEvent { Resource.ResourceId: "s1" });
        Assert.Equal(LlmAgentResourceStopStatus.Requested, await session.StopResourceAsync("s1", limit.Token));
        Assert.False(token.IsCancellationRequested);

        process.Push(Cancelled("c2", 7));
        await WaitFor(() => token.IsCancellationRequested);
        JsonElement answer = await AnswerFor(process, "c1");
        Assert.Equal(7, answer.GetProperty("id").GetInt32());
        Assert.True(answer.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("The tool call was cancelled.", answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Object, (await AnswerFor(process, "c2")).GetProperty("result").ValueKind);
        AssertMcpWire(process, "c1", "{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"The tool call was cancelled.\"}],\"isError\":true}}");
        AssertMcpWire(process, "c2", "{\"jsonrpc\":\"2.0\",\"id\":0,\"result\":{}}");

        process.Push(new { type = "result", subtype = "success", is_error = false, result = "" });
        List<LlmAgentEvent> turn = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);
        Assert.DoesNotContain(turn, item => item is LlmAgentToolEvent { ItemId: "tu1", Status: not "inProgress" });
    }

    private Task<ILlmAgentSession> Start(ManualProcess process, LlmConnection? connection = null) =>
        new ClaudeAgentClient(new Factory(process)).StartSessionAsync("connection", connection ?? Connection(), new() { ModelId = "claude-test", Runtime = new() { WorkingDirectory = _directory } });

    private Task<ILlmAgentSession> StartWithTool(ManualProcess process, LlmAgentToolHandler handler) =>
        new ClaudeAgentClient(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            Tools = [new LlmAgentToolDefinition { Name = "hold", Description = "Holds.", InputSchema = Json(new { type = "object" }) }],
            ToolHandler = handler,
        });

    private static object ToolUse(string id) => new
    {
        type = "assistant",
        message = new { id = "m-" + id, content = new object[] { new { type = "tool_use", id, name = "mcp__darkspyre-inference-connect__hold", input = new { } } } },
    };

    private static object ToolCall(string requestId, object id) => new
    {
        type = "control_request",
        request_id = requestId,
        request = new { subtype = "mcp_message", server_name = "darkspyre-inference-connect", message = new { method = "tools/call", @params = new { name = "hold", arguments = new { } }, jsonrpc = "2.0", id } },
    };

    private static object Cancelled(string requestId, object id) => new
    {
        type = "control_request",
        request_id = requestId,
        request = new { subtype = "mcp_message", server_name = "darkspyre-inference-connect", message = new { jsonrpc = "2.0", method = "notifications/cancelled", @params = new { requestId = id, reason = "AbortError: remote-cancel" } } },
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

    private static LlmConnection Connection() => new() { Version = "v1", Transport = LlmTransport.AnthropicClaudeCli, Endpoint = "claude" };

    private static void AssertMcpWire(ManualProcess process, string controlRequestId, string message)
    {
        string wire = Assert.Single(process.RawWrites(), line =>
        {
            JsonElement root = JsonElement.Parse(line);
            return root.GetProperty("type").GetString() == "control_response"
                && root.GetProperty("response").GetProperty("request_id").GetString() == controlRequestId;
        });
        string id = JsonElement.Parse(wire).GetProperty("response").GetProperty("request_id").GetRawText();
        Assert.Equal("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":" + id + ",\"response\":{\"mcp_response\":" + message + "}}}", wire);
        Assert.DoesNotContain('\n', wire);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static async Task WaitFor(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(10, limit.Token); }
    }

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

    private sealed class Factory(IClaudeCliSessionProcess process) : IClaudeCliSessionProcessFactory
    {
        public ClaudeCliSessionInvocation? Invocation { get; private set; }

        public Task<IClaudeCliSessionProcess> StartAsync(ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default)
        {
            Invocation = invocation;
            return Task.FromResult(process);
        }
    }

    /// <summary>A CLI whose output the test pushes. Control requests other than interrupt are answered with success.</summary>
    private sealed class ManualProcess : IClaudeCliSessionProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly ConcurrentQueue<string> _writes = new();

        public Task? DisposalRelease { get; init; }
        public Exception? DisposalFailure { get; init; }
        public Task? ToolWriteRelease { get; init; }
        public Task? ModelWriteRelease { get; init; }
        public Exception? ToolWriteFailure { get; init; }
        public bool WithholdStopReplies { get; init; }
        public TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ToolWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ModelWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals { get; private set; }

        public void Push(object value) => _output.Writer.TryWrite(JsonSerializer.Serialize(value));

        public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (ModelWriteRelease is not null && root.GetProperty("type").GetString() == "control_request"
                && root.GetProperty("request").GetProperty("subtype").GetString() == "set_model")
            {
                ModelWriteEntered.TrySetResult();
                await ModelWriteRelease;
            }
            if (ToolWriteRelease is not null && root.GetProperty("type").GetString() == "control_response")
            {
                ToolWriteEntered.TrySetResult();
                await ToolWriteRelease;
                if (ToolWriteFailure is not null) throw ToolWriteFailure;
            }
            _writes.Enqueue(line);
            if (root.GetProperty("type").GetString() == "control_request"
                && root.GetProperty("request").GetProperty("subtype").GetString() != "interrupt"
                && !(WithholdStopReplies && root.GetProperty("request").GetProperty("subtype").GetString() == "stop_task"))
            {
                Push(new { type = "control_response", response = new { subtype = "success", request_id = root.GetProperty("request_id").GetString(), response = new { } } });
            }
        }

        public IEnumerable<JsonElement> Writes() => _writes.Select(line => JsonSerializer.Deserialize<JsonElement>(line));

        public string[] RawWrites() => [.. _writes];

        /// <summary>The CLI exits: its output ends.</summary>
        public void Exit() => _output.Writer.TryComplete();

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

        public async ValueTask DisposeAsync()
        {
            Disposals++;
            DisposalEntered.TrySetResult();
            _output.Writer.TryComplete();
            if (DisposalRelease is not null) await DisposalRelease;
            if (DisposalFailure is not null) throw DisposalFailure;
        }
    }
}
