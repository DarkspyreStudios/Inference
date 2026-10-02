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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostTurnsProjectRunningUsageAndHonorModelOverrides(bool enabled)
    {
        var process = new ManualProcess();
        var connection = Connection() with
        {
            CapabilityOverrides = new() { SupportsUsageDeltas = !enabled },
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
            { ["claude-test"] = new() { SupportsUsageDeltas = enabled } }
        };
        await using ILlmAgentSession session = await Start(process, connection);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<List<LlmAgentEvent>> host = Collect(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hi")] }, limit.Token));
        string uuid = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "user", isReplay = true, uuid, message = new { role = "user", content = "hi" } });
        process.Push(UsageStart("subagent", parent: "tool-1"));
        process.Push(UsageStart("m1"));
        process.Push(UsageDelta(2));
        process.Push(UsageDelta(2));
        process.Push(new { type = "result", uuid = "r1", subtype = "success", result = "Hello.", usage = new { input_tokens = 8, output_tokens = 3 } });
        List<LlmAgentEvent> events = await host;

        LlmAgentUsageEvent[] usage = [.. events.OfType<LlmAgentUsageEvent>()];
        Assert.Equal(enabled ? 3 : 1, usage.Length);
        if (enabled)
        {
            Assert.Equal(new LlmUsage(7, null, null), usage[0].Usage);
            Assert.Equal(new LlmUsage(7, 2, 9), usage[1].Usage);
        }
        Assert.Equal(new LlmUsage(8, 3, 11), usage[^1].Usage);
        Assert.All(usage, item =>
        {
            Assert.Equal(usage[0].RequestId, item.RequestId);
            Assert.Equal(item.Usage, item.RunTotals);
            Assert.Equal(events[^1].TurnId, item.TurnId);
        });
        Assert.True(events.IndexOf(usage[^1]) < events.Count - 1);
        Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderTurnsReplaceRunningUsageWithAnUnknownFinal(bool enabled)
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Start(process, Connection() with
        { CapabilityOverrides = new() { SupportsUsageDeltas = enabled } });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var feed = session.ObserveAsync(limit.Token).GetAsyncEnumerator(limit.Token);
        process.Push(UsageStart("m1"));
        process.Push(UsageDelta(2));
        process.Push(new { type = "result", uuid = "r1", subtype = "success", result = "Done." });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);

        LlmAgentUsageEvent[] usage = [.. events.OfType<LlmAgentUsageEvent>()];
        Assert.Equal(enabled ? 3 : 1, usage.Length);
        Assert.Null(usage[^1].Usage);
        Assert.Null(usage[^1].RunTotals);
        Assert.Equal(Assert.Single(events.OfType<LlmAgentProviderTurnStartedEvent>()).TurnId, usage[^1].TurnId);
        Assert.All(usage, item => Assert.Equal(usage[0].RequestId, item.RequestId));
        Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task ReplayedCallsAndResultsCannotChargeOrEndTheNextTurn()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Start(process);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<List<LlmAgentEvent>> first = Collect(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("one")] }, limit.Token));
        string firstId = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "user", isReplay = true, uuid = firstId, message = new { role = "user", content = "one" } });
        process.Push(UsageStart("m1"));
        process.Push(UsageDelta(2));
        process.Push(new { type = "result", uuid = "r1", subtype = "success", result = "First.", usage = new { input_tokens = 7, output_tokens = 2 } });
        await first;

        Task<List<LlmAgentEvent>> second = Collect(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("two")] }, limit.Token));
        string secondId = await process.WaitForUserMessage(limit.Token, index: 1);
        process.Push(new { type = "user", isReplay = true, uuid = secondId, message = new { role = "user", content = "two" } });
        process.Push(new { type = "result", uuid = "r1", subtype = "success", result = "First.", usage = new { input_tokens = 7, output_tokens = 2 } });
        process.Push(UsageStart("m1"));
        process.Push(UsageDelta(99));
        process.Push(UsageStart("m2"));
        process.Push(UsageDelta(3));
        process.Push(new { type = "result", uuid = "r2", subtype = "success", result = "Second.", usage = new { input_tokens = 8, output_tokens = 4 } });
        List<LlmAgentEvent> events = await second;

        LlmAgentUsageEvent[] usage = [.. events.OfType<LlmAgentUsageEvent>()];
        Assert.Equal(new int?[] { null, 3, 4 }, usage.Select(item => item.Usage?.OutputTokens));
        Assert.Equal(new int?[] { 7, 7, 8 }, usage.Select(item => item.Usage?.InputTokens));
        Assert.Equal("Second.", Assert.Single(events.OfType<LlmAgentMessageCompletedEvent>()).Text);
        Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]);
    }

    private static object UsageStart(string id, string? parent = null) => new
    {
        type = "stream_event",
        parent_tool_use_id = parent,
        @event = new { type = "message_start", message = new { id, usage = new { input_tokens = 7, output_tokens = 1 } } }
    };

    private static object UsageDelta(int output) => new
    { type = "stream_event", @event = new { type = "message_delta", usage = new { output_tokens = output } } };

    [Fact]
    public async Task CommandTasksFollowTheirMessagesAndOutputExcludesTheTrailer()
    {
        var events = new ConcurrentQueue<LlmAgentEvent>();
        await using var tasks = new ClaudeBackgroundTasks("thread", 1_048_576, events.Enqueue, TimeSpan.FromMilliseconds(20));
        string path = Path.Combine(_directory, "b1.output");
        await File.WriteAllTextAsync(path, "line one\n");

        tasks.Handle(Json(new { type = "system", subtype = "background_tasks_changed", tasks = new[] { new { task_id = "b1", task_type = "local_bash", description = "sleep" } } }));
        tasks.Handle(Json(new { type = "system", subtype = "task_started", task_id = "b1", tool_use_id = "tu1", description = "sleep", is_backgrounded = true, task_type = "local_bash" }));
        tasks.ToolResult("tu1", $"Command running in background with ID: b1. Output is being written to: {path}. You will be notified.");
        await WaitFor(() => events.OfType<LlmAgentResourceOutputEvent>().Any(item => item.ResourceId == "b1"));

        LlmAgentResourceOutput first = await tasks.ReadAsync("b1", null, CancellationToken.None);
        Assert.Equal(("line one\n", "9"), (Encoding.UTF8.GetString(first.Data.Span), first.NextCursor));
        await File.AppendAllTextAsync(path, "line two\n\n[exited with code 3]");
        tasks.Handle(Json(new { type = "system", subtype = "task_updated", task_id = "b1", patch = new { status = "completed", end_time = 1_790_270_769_617L } }));
        tasks.Handle(Json(new { type = "system", subtype = "task_notification", task_id = "b1", tool_use_id = "tu1", status = "completed", output_file = path, summary = "done" }));

        LlmAgentResourceOutput second = await tasks.ReadAsync("b1", first.NextCursor, CancellationToken.None);
        LlmAgentResourceOutput after = await tasks.ReadAsync("b1", second.NextCursor, CancellationToken.None);
        Assert.Equal(("line two\n", true), (Encoding.UTF8.GetString(second.Data.Span), second.Closed));
        Assert.Equal((LlmAgentResourceOutputStatus.NoData, true), (after.Status, after.Closed));
        LlmAgentResource ended = Assert.Single(tasks.Snapshot());
        Assert.Equal((LlmAgentResourceState.Completed, 3, "tu1", "done"), (ended.State, ended.ExitCode, ended.OriginToolCallId, ended.Summary));
        Assert.Equal(LlmOperationSupport.Supported, ended.Support.Output);
        Assert.Equal(LlmOperationSupport.Unsupported, ended.Support.Terminal);
        Assert.Equal(
            [LlmAgentResourceChange.Added, LlmAgentResourceChange.Updated, LlmAgentResourceChange.Updated, LlmAgentResourceChange.Ended,
                LlmAgentResourceChange.Updated],
            events.OfType<LlmAgentResourceEvent>().Select(item => item.Change));
        Assert.Equal("b1", tasks.TakeLastReported());
        Assert.Null(tasks.TakeLastReported());
    }

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
    public async Task SubagentOutputIsItsOwnAndRetentionAdvancesTheEarliestCursor()
    {
        var events = new ConcurrentQueue<LlmAgentEvent>();
        await using var tasks = new ClaudeBackgroundTasks("thread", 16, events.Enqueue, TimeSpan.FromMinutes(1));
        tasks.Handle(Json(new { type = "system", subtype = "task_started", task_id = "a1", tool_use_id = "agent-call", description = "check", subagent_type = "general-purpose", is_backgrounded = true, task_type = "local_agent" }));
        tasks.SubagentMessage("agent-call", Json(new
        {
            role = "assistant",
            content = new object[] { new { type = "text", text = "hello world 12345" }, new { type = "tool_use", id = "sub-bash", name = "Bash", input = new { command = "ls" } } }
        }));
        tasks.Handle(Json(new { type = "system", subtype = "task_started", task_id = "c1", owned_by_subagent = true, tool_use_id = "sub-bash", description = "ls", is_backgrounded = false, task_type = "local_bash" }));
        tasks.Handle(Json(new { type = "system", subtype = "task_progress", task_id = "a1", description = "Running ls", usage = new { total_tokens = 100, tool_uses = 1, duration_ms = 50 } }));

        LlmAgentResourceOutput gap = await tasks.ReadAsync("a1", "0", CancellationToken.None);
        LlmAgentResourceOutput kept = await tasks.ReadAsync("a1", gap.EarliestCursor, CancellationToken.None);

        Assert.Equal(LlmAgentResourceOutputStatus.Gap, gap.Status);
        Assert.Equal(16, kept.Data.Length);
        Assert.EndsWith("\"ls\"}]\n", Encoding.UTF8.GetString(kept.Data.Span), StringComparison.Ordinal);
        Assert.Equal("a1", tasks.Snapshot().Single(item => item.ResourceId == "c1").ParentResourceId);
        LlmAgentResource agent = tasks.Snapshot().Single(item => item.ResourceId == "a1");
        Assert.Equal(("general-purpose", 100L, "Running ls"), (agent.AgentType, agent.Usage!.TotalTokens, agent.Summary));
        Assert.Contains(events.OfType<LlmAgentResourceOutputEvent>(), item => item.ResourceId == "a1");
    }

    [Fact]
    public async Task WorkMissingFromTwoSetMessagesEndsAsUnknownAndProcessEndEndsTheRest()
    {
        var events = new ConcurrentQueue<LlmAgentEvent>();
        await using var tasks = new ClaudeBackgroundTasks("thread", 1024, events.Enqueue, TimeSpan.FromMinutes(1));
        tasks.Handle(Json(new { type = "system", subtype = "background_tasks_changed", tasks = new[] { new { task_id = "x1", task_type = "local_bash", description = "one" } } }));
        tasks.Handle(Json(new { type = "system", subtype = "background_tasks_changed", tasks = Array.Empty<object>() }));
        Assert.Null(Assert.Single(tasks.Snapshot()).Ended); // Its own end message may still follow.
        tasks.Handle(Json(new { type = "system", subtype = "background_tasks_changed", tasks = new[] { new { task_id = "x2", task_type = "local_agent", description = "two" } } }));

        Assert.Equal(LlmAgentResourceState.Unknown, tasks.Snapshot().Single(item => item.ResourceId == "x1").State);
        tasks.EndAll();
        Assert.All(tasks.Snapshot(), item => Assert.NotNull(item.Ended));
        Assert.Equal(LlmAgentResourceState.Unknown, tasks.Snapshot().Single(item => item.ResourceId == "x2").State);
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
    public async Task AHostTurnSentDuringAProviderTurnStartsAtItsEcho()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Start(process);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.ObserveAsync(limit.Token).GetAsyncEnumerator(limit.Token);
        process.Push(new { type = "assistant", message = new { id = "m1", content = new object[] { new { type = "text", text = "Working." } } } });
        LlmAgentProviderTurnStartedEvent provider = (LlmAgentProviderTurnStartedEvent)(await ReadUntil(feed, item => item is LlmAgentProviderTurnStartedEvent))[^1];

        Task<List<LlmAgentEvent>> host = Collect(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hi")] }, limit.Token));
        string uuid = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "Working." });
        process.Push(new { type = "user", isReplay = true, uuid, message = new { role = "user", content = "hi" } });
        process.Push(new { type = "assistant", message = new { id = "m2", content = new object[] { new { type = "text", text = "Hello." } } } });
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "Hello." });

        List<LlmAgentEvent> providerEvents = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);
        List<LlmAgentEvent> hostEvents = await host;
        Assert.Equal("Working.", Assert.Single(providerEvents.OfType<LlmAgentMessageCompletedEvent>()).Text);
        Assert.Equal(provider.TurnId, providerEvents[^1].TurnId);
        Assert.Equal("Hello.", Assert.Single(hostEvents.OfType<LlmAgentMessageCompletedEvent>()).Text);
        Assert.Equal(LlmAgentTurnStatus.Completed, Assert.IsType<LlmAgentTurnCompletedEvent>(hostEvents[^1]).Status);
        Assert.NotEqual(provider.TurnId, hostEvents[^1].TurnId);
    }

    [Fact]
    public async Task AProviderTurnCanBeInterruptedAndResourcesStopThroughTheCli()
    {
        var process = new ManualProcess();
        await using ILlmAgentSession session = await Start(process);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.ObserveAsync(limit.Token).GetAsyncEnumerator(limit.Token);
        process.Push(new { type = "assistant", message = new { id = "m1", content = new object[] { new { type = "text", text = "Working." } } } });
        LlmAgentProviderTurnStartedEvent provider = (LlmAgentProviderTurnStartedEvent)(await ReadUntil(feed, item => item is LlmAgentProviderTurnStartedEvent))[^1];

        await session.InterruptAsync(provider.TurnId, limit.Token);
        process.Push(new { type = "system", subtype = "task_started", task_id = "s1", tool_use_id = "tu-s1", description = "server", is_backgrounded = true, task_type = "local_bash" });
        await ReadUntil(feed, item => item is LlmAgentResourceEvent { Resource.ResourceId: "s1" });

        Assert.Equal(LlmAgentResourceStopStatus.Requested, await session.StopResourceAsync("s1", limit.Token));
        Assert.Equal(LlmAgentResourceStopStatus.Unknown, await session.StopResourceAsync("missing", limit.Token));
        process.Push(new { type = "system", subtype = "task_notification", task_id = "s1", tool_use_id = "tu-s1", status = "stopped", output_file = "", summary = "server" });
        await ReadUntil(feed, item => item is LlmAgentResourceEvent { Change: LlmAgentResourceChange.Ended });
        Assert.Equal(LlmAgentResourceStopStatus.Exited, await session.StopResourceAsync("s1", limit.Token));
        Assert.Contains(process.Requests(), request => request.GetProperty("subtype").GetString() == "interrupt");
        Assert.Contains(process.Requests(), request => request.GetProperty("subtype").GetString() == "stop_task"
            && request.GetProperty("task_id").GetString() == "s1");
        AssertControlWire(process, "interrupt", "{\"subtype\":\"interrupt\",\"cancel_queued\":false}");
        AssertControlWire(process, "stop_task", "{\"subtype\":\"stop_task\",\"task_id\":\"s1\"}");
        Assert.Equal(LlmAgentResourceState.Stopped, Assert.Single(await session.GetResourcesAsync(limit.Token)).State);
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

    [Fact]
    public async Task AToolResultAfterItsTurnEndedDoesNotReachTheNextTurn()
    {
        var process = new ManualProcess();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ILlmAgentSession session = await StartWithTool(process, async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return LlmToolOutput.FromText("late");
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.ObserveAsync(limit.Token).GetAsyncEnumerator(limit.Token);
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 3));
        await entered.Task.WaitAsync(limit.Token);
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "" });
        await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);

        Task<List<LlmAgentEvent>> host = Collect(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("next")] }, limit.Token));
        string uuid = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "user", isReplay = true, uuid, message = new { role = "user", content = "next" } });
        await WaitFor(() => process.Writes().Any(root => root.GetProperty("type").GetString() == "user"));
        release.TrySetResult();
        JsonElement answer = await AnswerFor(process, "c1");
        Assert.Equal("late", answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        process.Push(new { type = "assistant", message = new { id = "m2", content = new object[] { new { type = "text", text = "Next." } } } });
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "Next." });

        List<LlmAgentEvent> events = await host;
        Assert.DoesNotContain(events, item => item is LlmAgentToolEvent);
        Assert.Equal("Next.", Assert.Single(events.OfType<LlmAgentMessageCompletedEvent>()).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingTheSessionOrTheCliCancelsHostToolCalls(bool exit)
    {
        var process = new ManualProcess();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        ILlmAgentSession session = await StartWithTool(process, async (_, token) =>
        {
            entered.TrySetResult(token);
            await Task.Delay(Timeout.Infinite, token);
            return LlmToolOutput.FromText("unreachable");
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 9));
        CancellationToken token = await entered.Task.WaitAsync(limit.Token);

        if (exit) process.Exit();
        else await session.DisposeAsync().AsTask().WaitAsync(limit.Token);
        await WaitFor(() => token.IsCancellationRequested);
        if (exit) await session.DisposeAsync().AsTask().WaitAsync(limit.Token);
    }

    [Theory]
    [InlineData(LlmAgentSandboxMode.WorkspaceWrite, false, "Read,Write,Edit,Bash,WebFetch,WebSearch", null, false)]
    [InlineData(LlmAgentSandboxMode.WorkspaceWrite, true, "Read,Write,Edit,Bash,WebFetch,WebSearch,Task,Monitor", "Task,Monitor", false)]
    [InlineData(LlmAgentSandboxMode.ReadOnly, true, "Read,Task", "Task", false)]
    [InlineData(LlmAgentSandboxMode.DangerFullAccess, false, "default", null, true)]
    [InlineData(LlmAgentSandboxMode.DangerFullAccess, true, "default", "Task,Monitor", false)]
    public async Task BackgroundToolsFollowTheSessionOption(LlmAgentSandboxMode sandbox, bool allow, string tools, string? allowed, bool disallowed)
    {
        var process = new ManualProcess();
        var factory = new Factory(process);
        await using ILlmAgentSession session = await new ClaudeAgentClient(factory).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new()
            {
                WorkingDirectory = _directory,
                Policy = new LlmAgentHostRuntimePolicy { Sandbox = sandbox, AllowRuntimeBackgroundWork = allow }
            }
        });

        IReadOnlyList<string> arguments = factory.Invocation!.Arguments;
        Assert.Contains("--replay-user-messages", arguments);
        Assert.Equal(tools, arguments[arguments.ToList().IndexOf("--tools") + 1]);
        Assert.Equal(allowed, arguments.Contains("--allowedTools") ? arguments[arguments.ToList().IndexOf("--allowedTools") + 1] : null);
        Assert.Equal(disallowed, arguments.Contains("--disallowedTools"));
        Assert.Equal(LlmOperationSupport.Supported, (await session.GetCapabilitiesAsync()).ResourceObservation);
    }

    [Fact]
    public async Task RuntimesThatReportNoBackgroundWorkRefuseItAndReportNothing()
    {
        Assert.Throws<NotSupportedException>(() => AgentResourceOptions.Validate(ManagedAgentPolicy.Capabilities, true, 1));
        Assert.Throws<NotSupportedException>(() => AgentResourceOptions.Validate(NativeAgentContract.AgentCore, true, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentResourceOptions.Validate(NativeAgentContract.Claude, false, 0));
        AgentResourceOptions.Validate(NativeAgentContract.Claude, true, 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ClaudeAgentClient(new Factory(new ManualProcess()))
            .StartSessionAsync("connection", Connection(), new() { ModelId = "claude-test", Runtime = new() { WorkingDirectory = _directory }, ResourceOutputRetentionBytes = 0 }));

        var none = new NoAgentResources(LlmAgentResourceStopStatus.Unsupported);
        Task observed = Task.Run(async () => { await foreach (LlmAgentEvent _ in none.ObserveAsync(CancellationToken.None)) { } });
        Assert.False(observed.IsCompleted);
        none.Dispose();
        await observed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await NoAgentResources.GetAsync());
        Assert.Equal(LlmAgentResourceOutputStatus.Unavailable, (await NoAgentResources.ReadAsync()).Status);
        Assert.Equal(LlmAgentResourceStopStatus.Unsupported, await none.StopAsync());
        Assert.Equal(LlmOperationSupport.Unknown, NativeAgentContract.AgentCore.ResourceObservation);
        Assert.Equal(LlmOperationSupport.Unsupported, ManagedAgentPolicy.Capabilities.ResourceObservation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionRetirementIsSharedFencesOperationsAndPreservesProcessFailure(bool fail)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processFailure = new IOException("controlled process retirement failure");
        var process = new ManualProcess { DisposalRelease = release.Task, DisposalFailure = fail ? processFailure : null };
        ILlmAgentSession session = await Start(process);
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.ObserveAsync().GetAsyncEnumerator();
        Task<bool> observation = feed.MoveNextAsync().AsTask();
        Task retirement = session.DisposeAsync().AsTask();
        try
        {
            await process.DisposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(retirement.IsCompleted);
            Assert.Same(retirement, session.DisposeAsync().AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetCapabilitiesAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetResourcesAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StopResourceAsync("missing"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ReadResourceOutputAsync("missing", null));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Collect(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("late")] })));
            release.TrySetResult();
            if (fail) Assert.Same(processFailure, await Assert.ThrowsAsync<IOException>(() => retirement.WaitAsync(TimeSpan.FromSeconds(5))));
            else await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, process.Disposals);
            Assert.Same(retirement, session.DisposeAsync().AsTask());
        }
        finally
        {
            release.TrySetResult();
            _ = await Record.ExceptionAsync(() => retirement.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetirementWaitsForIgnoringHostToolsAndPreservesCleanupFailures(bool failProcess, bool failCallback)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processFailure = new IOException("controlled process failure");
        var callbackFailure = new InvalidOperationException("controlled callback failure");
        var process = new ManualProcess { DisposalFailure = failProcess ? processFailure : null };
        ILlmAgentSession session = await StartWithTool(process, async (_, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                stopped.TrySetResult();
                if (failCallback) throw callbackFailure;
            });
            entered.TrySetResult();
            await release.Task;
            return LlmToolOutput.FromText("late");
        });
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 9));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task retirement = session.DisposeAsync().AsTask();
        try
        {
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, process.Disposals);
            Assert.Same(retirement, session.DisposeAsync().AsTask());
            release.TrySetResult();
            Exception? failure = await Record.ExceptionAsync(() => retirement.WaitAsync(TimeSpan.FromSeconds(5)));
            if (!failProcess && !failCallback) Assert.Null(failure);
            else
            {
                Assert.NotNull(failure);
                IReadOnlyCollection<Exception> errors = failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [failure];
                if (failProcess) Assert.Contains(processFailure, errors);
                if (failCallback) Assert.Contains(callbackFailure, errors);
            }
            Assert.Equal(1, process.Disposals);
            Assert.DoesNotContain(process.Writes(), root => root.GetProperty("type").GetString() == "control_response");
        }
        finally
        {
            release.TrySetResult();
            _ = await Record.ExceptionAsync(() => retirement.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementWaitsForTheFullHostToolResponseWrite(bool fail)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeFailure = new IOException("controlled tool response failure");
        var process = new ManualProcess { ToolWriteRelease = release.Task, ToolWriteFailure = fail ? writeFailure : null };
        ILlmAgentSession session = await StartWithTool(process, (_, _) => Task.FromResult(LlmToolOutput.FromText("done")));
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 9));
        await process.ToolWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task retirement = session.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, process.Disposals);
            Assert.Same(retirement, session.DisposeAsync().AsTask());
            release.TrySetResult();
            if (fail) Assert.Same(writeFailure, await Assert.ThrowsAsync<IOException>(() => retirement.WaitAsync(TimeSpan.FromSeconds(5))));
            else await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, process.Disposals);
        }
        finally
        {
            release.TrySetResult();
            _ = await Record.ExceptionAsync(() => retirement.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task RetirementWaitsForAPausedTurnEnumeratorBeforeDisposingItsGate()
    {
        var process = new ManualProcess();
        ILlmAgentSession session = await Start(process);
        IAsyncEnumerator<LlmAgentEvent> turn = session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hi")] }).GetAsyncEnumerator();
        Assert.True(await turn.MoveNextAsync());
        Task retirement = session.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, process.Disposals);
            await turn.DisposeAsync();
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(process.Writes(), root => root.GetProperty("type").GetString() == "user");
        }
        finally
        {
            await turn.DisposeAsync();
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurnAttachmentsAreFrozenBeforeControlAcknowledgmentAndPausedStart(bool holdControl)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new ManualProcess { ModelWriteRelease = holdControl ? release.Task : null };
        ILlmAgentSession session = await Start(process);
        byte[] bytes = "original"u8.ToArray();
        List<LlmMessageAttachment> attachments = [new LlmAttachment { Kind = LlmBinaryKind.Document,
            FileName = "original.txt", MediaType = "text/plain", Data = bytes }];
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        IAsyncEnumerator<LlmAgentEvent> turn = session.RunTurnAsync(new()
        { ModelId = holdControl ? "claude-changed" : null, Input = [new LlmAgentTextContent("hello")], Attachments = attachments }, limit.Token)
            .GetAsyncEnumerator(limit.Token);
        Task<bool> first = turn.MoveNextAsync().AsTask();
        Task? drain = null;
        try
        {
            if (holdControl) await process.ModelWriteEntered.Task.WaitAsync(limit.Token);
            else Assert.True(await first);
            bytes[0] = (byte)'X';
            attachments.Add(new LlmAttachment { Kind = LlmBinaryKind.Document, FileName = "late.txt",
                MediaType = "text/plain", Data = "late"u8.ToArray() });
            release.TrySetResult();
            if (holdControl) Assert.True(await first);
            Assert.IsType<LlmAgentTurnStartedEvent>(turn.Current);
            drain = DrainTurnAsync();
            string uuid = await process.WaitForUserMessage(limit.Token);
            JsonElement user = Assert.Single(process.Writes(), root => root.GetProperty("type").GetString() == "user");
            JsonElement content = user.GetProperty("message").GetProperty("content");
            JsonElement document = Assert.Single(content.EnumerateArray(), part => part.GetProperty("type").GetString() == "document");
            Assert.Equal("text", document.GetProperty("source").GetProperty("type").GetString());
            Assert.Equal("original", document.GetProperty("source").GetProperty("data").GetString());
            process.Push(new { type = "user", isReplay = true, uuid, message = new { role = "user", content = "hello" } });
            process.Push(new { type = "result", uuid = "result", subtype = "success", result = "actual" });
            await drain;
        }
        finally
        {
            Task retirement = session.DisposeAsync().AsTask();
            release.TrySetResult();
            try { await first.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) when (first.IsCompleted) { }
            if (drain is not null)
            {
                try { await drain.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) when (drain.IsCompleted) { }
            }
            try { await turn.DisposeAsync(); }
            finally { await retirement.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        async Task DrainTurnAsync()
        { while (await turn.MoveNextAsync()) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NumericAndStringToolRequestIdsHaveIndependentCancellation(bool cancelString)
    {
        var entered = new ConcurrentDictionary<string, CancellationToken>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new ManualProcess();
        await using ILlmAgentSession session = await StartWithTool(process, async (call, token) =>
        {
            entered.TryAdd(call.CallId, token);
            await release.Task;
            return LlmToolOutput.FromText("done");
        });
        try
        {
            process.Push(ToolUse("tu1"));
            process.Push(ToolCall("c1", 9.0m));
            process.Push(ToolUse("tu2"));
            process.Push(ToolCall("c2", "9"));
            await WaitFor(() => entered.Count == 2);
            process.Push(Cancelled("cancel", cancelString ? "9" : (object)9));
            await AnswerFor(process, "cancel");
            Assert.Equal(!cancelString, entered["tu1"].IsCancellationRequested);
            Assert.Equal(cancelString, entered["tu2"].IsCancellationRequested);
            release.TrySetResult();
            string cancelledControlId = cancelString ? "c2" : "c1";
            string successfulControlId = cancelString ? "c1" : "c2";
            string cancelledId = cancelString ? "\"9\"" : "9.0";
            string successfulId = cancelString ? "9.0" : "\"9\"";
            Assert.True((await AnswerFor(process, cancelledControlId)).GetProperty("result").GetProperty("isError").GetBoolean());
            JsonElement answer = await AnswerFor(process, successfulControlId);
            Assert.Equal(successfulId, answer.GetProperty("id").GetRawText());
            Assert.Equal("done", answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
            AssertMcpWire(process, "cancel", "{\"jsonrpc\":\"2.0\",\"id\":0,\"result\":{}}");
            AssertMcpWire(process, cancelledControlId, "{\"jsonrpc\":\"2.0\",\"id\":" + cancelledId + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"The tool call was cancelled.\"}],\"isError\":true}}");
            AssertMcpWire(process, successfulControlId, "{\"jsonrpc\":\"2.0\",\"id\":" + successfulId + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"done\"}],\"isError\":false}}");
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task RetirementCancelsAnActiveHostTurnAndItsPendingResourceStop()
    {
        var process = new ManualProcess { WithholdStopReplies = true };
        ILlmAgentSession session = await Start(process);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<List<LlmAgentEvent>> turn = Collect(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hi")] }));
        string uuid = await process.WaitForUserMessage(limit.Token);
        process.Push(new { type = "user", isReplay = true, uuid, message = new { role = "user", content = "hi" } });
        process.Push(new { type = "system", subtype = "task_started", task_id = "s1", tool_use_id = "tu-s1", description = "server", is_backgrounded = true, task_type = "local_bash" });
        await WaitFor(() => session.GetResourcesAsync().GetAwaiter().GetResult().Any(item => item.ResourceId == "s1"));
        Task<LlmAgentResourceStopStatus> stopping = session.StopResourceAsync("s1");
        await WaitFor(() => process.Requests().Any(request => request.GetProperty("subtype").GetString() == "stop_task"));

        await session.DisposeAsync().AsTask().WaitAsync(limit.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping);
        Assert.Equal(1, process.Disposals);
    }

    [Fact]
    public async Task DuplicateActiveToolRequestIdsFailWithoutLosingTheFirstCallback()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new ManualProcess();
        int invocations = 0;
        ILlmAgentSession session = await StartWithTool(process, async (_, token) =>
        {
            Interlocked.Increment(ref invocations);
            entered.TrySetResult(token);
            await release.Task;
            return LlmToolOutput.FromText("late");
        });
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 9));
        CancellationToken token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        process.Push(ToolUse("tu2"));
        process.Push(ToolCall("c2", 9));
        try
        {
            await WaitFor(() => token.IsCancellationRequested);
            Assert.Equal(1, Volatile.Read(ref invocations));
            Task retirement = session.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            release.TrySetResult();
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ProcessExitCancelsEveryToolAndClosesObservationDespiteAThrowingCallback()
    {
        var entered = new ConcurrentDictionary<string, CancellationToken>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFailure = new InvalidOperationException("controlled live cancellation failure");
        var process = new ManualProcess();
        ILlmAgentSession session = await StartWithTool(process, async (call, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                if (call.CallId == "tu1") throw callbackFailure;
            });
            entered.TryAdd(call.CallId, token);
            await release.Task;
            return LlmToolOutput.FromText("late");
        });
        Task<List<LlmAgentEvent>> observation = Collect(session.ObserveAsync());
        try
        {
            process.Push(ToolUse("tu1"));
            process.Push(ToolCall("c1", 1));
            process.Push(ToolUse("tu2"));
            process.Push(ToolCall("c2", 2));
            process.Push(new { type = "system", subtype = "task_started", task_id = "s1", tool_use_id = "tu-s1", description = "server", is_backgrounded = true, task_type = "local_bash" });
            await WaitFor(() => entered.Count == 2 && session.GetResourcesAsync().GetAwaiter().GetResult().Count == 1);
            process.Exit();

            List<LlmAgentEvent> events = await observation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(entered.Values, token => Assert.True(token.IsCancellationRequested));
            Assert.Contains(events, item => item is LlmAgentResourceEvent { Resource.State: LlmAgentResourceState.Unknown, Resource.Ended: not null });
            Task retirement = session.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            release.TrySetResult();
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => retirement.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(callbackFailure, failure.Flatten().InnerExceptions);
            Assert.Same(retirement, session.DisposeAsync().AsTask());
        }
        finally
        {
            release.TrySetResult();
            _ = await Record.ExceptionAsync(() => session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            _ = await Record.ExceptionAsync(() => observation.WaitAsync(TimeSpan.FromSeconds(5)));
        }
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

    private static void AssertControlWire(ManualProcess process, string subtype, string request)
    {
        string wire = Assert.Single(process.RawWrites(), line =>
        {
            JsonElement root = JsonElement.Parse(line);
            return root.GetProperty("type").GetString() == "control_request"
                && root.GetProperty("request").GetProperty("subtype").GetString() == subtype;
        });
        string id = JsonElement.Parse(wire).GetProperty("request_id").GetRawText();
        Assert.Equal("{\"type\":\"control_request\",\"request_id\":" + id + ",\"request\":" + request + "}", wire);
        Assert.DoesNotContain('\n', wire);
    }

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

    private static async Task<List<LlmAgentEvent>> Collect(IAsyncEnumerable<LlmAgentEvent> source)
    {
        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in source) { events.Add(item); }
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
