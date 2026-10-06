using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ClaudeAgentResourcesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("connect-claude-resources-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("result.json", false)]
    [InlineData("result.json", true)]
    [InlineData("result.pdf", true)]
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
        await using var providers = new LlmAgentProviderService(new ResourcePreparationTests.Source(Connection()), [new ClaudeAgentClient(new Factory(process))]);
        await using ILlmAgentSession session = await providers.StartSessionAsync("connection", new()
        {
            ModelId = "claude-test",
            Context = restored,
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
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
    public async Task NativeOpeningRejectsADifferentSelectionBeforeStartingTheProcess()
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 4, [reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray())]);
        var factory = new Factory(new ManualProcess());
        var client = new ClaudeAgentClient(factory);
        await Assert.ThrowsAsync<ArgumentException>(() => client.StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Context = new() { ResourceSelectionVersion = new("workspace", "session", 3), SelectedResources = [reference] },
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
            CallerServices = new() { Resources = access },
            Runtime = new() { WorkingDirectory = _directory }
        }));
        Assert.Null(factory.Invocation);
        Assert.Empty(access.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeResumedAndForkedPromptsCarryTheBoundSelection(bool fork)
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 4, [reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray())]);
        var process = new ManualProcess();
        var client = new ClaudeAgentClient(new Factory(process));
        var options = new LlmAgentResumeOptions
        { ModelId = "claude-test", CallerServices = new() { Resources = access }, Runtime = new() { WorkingDirectory = _directory } };
        string threadId = Guid.NewGuid().ToString();
        await using ILlmAgentSession session = fork
            ? await client.ForkSessionAsync("connection", Connection(), threadId, options: options)
            : await client.ResumeSessionAsync("connection", Connection(), threadId, options);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(
            new() { Input = [new LlmAgentTextContent("Read the file")] }, limit.Token).GetAsyncEnumerator(limit.Token);
        Assert.True(await feed.MoveNextAsync());
        string turnId = await process.WaitForUserMessage(limit.Token);
        JsonElement input = Assert.Single(process.Writes(), item => item.GetProperty("type").GetString() == "user");
        JsonElement document = Assert.Single(input.GetProperty("message").GetProperty("content").EnumerateArray(),
            part => part.GetProperty("type").GetString() == "document");
        Assert.Equal("reference text", document.GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("reference.txt", document.GetProperty("title").GetString());
        Assert.Single(access.Reads);
        Assert.False(access.Reads[0].CanRead);
        process.Push(new { type = "user", uuid = turnId, isReplay = true });
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "answer", stop_reason = "end_turn" });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Equal(4, access.Selection.Revision);

        access.Selection = new("workspace", "session", 5, []);
        await using IAsyncEnumerator<LlmAgentEvent> next = session.RunTurnAsync(
            new() { Input = [new LlmAgentTextContent("Continue")] }, limit.Token).GetAsyncEnumerator(limit.Token);
        Assert.True(await next.MoveNextAsync());
        string nextTurnId = await process.WaitForUserMessage(limit.Token, index: 1);
        JsonElement nextInput = process.Writes().Last(item => item.GetProperty("type").GetString() == "user");
        Assert.DoesNotContain(nextInput.GetProperty("message").GetProperty("content").EnumerateArray(),
            part => part.GetProperty("type").GetString() == "document");
        process.Push(new { type = "user", uuid = nextTurnId, isReplay = true });
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "answer", stop_reason = "end_turn" });
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single((await ReadUntil(next,
            item => item is LlmAgentTurnCompletedEvent)).OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Single(access.Reads);
        Assert.Equal(5, access.Selection.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeInputCleanupRefusalPreventsTheWriteAndRemainsVisibleAtRetirement(bool invalidContent)
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var cleanup = new IOException("The resource reader refuses release.");
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 1, [reference]),
            [(reference, "reference.txt", invalidContent ? new byte[] { 0xff } : "reference text"u8.ToArray())], cleanup);
        var process = new ManualProcess();
        ILlmAgentSession session = await new ClaudeAgentClient(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        { ModelId = "claude-test", CallerServices = new() { Resources = access }, Runtime = new() { WorkingDirectory = _directory } });
        Exception failure = (await Record.ExceptionAsync(async () =>
        {
            await foreach (LlmAgentEvent _ in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Read the file")] })) { }
        }))!;
        Exception release = failure;
        if (invalidContent)
        {
            Exception[] causes = Assert.IsType<AggregateException>(failure).InnerExceptions.ToArray();
            Assert.Equal(2, causes.Length);
            Assert.IsType<NotSupportedException>(Assert.IsType<InferenceResourceOperationException>(causes[0]).InnerException);
            release = causes[1];
        }
        var refused = Assert.IsType<InferenceResourceOperationException>(release);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, refused.Failure.Phase);
        Assert.Same(cleanup, refused.InnerException);
        Assert.DoesNotContain(process.Writes(), item => item.GetProperty("type").GetString() == "user");
        Exception retired = (await Record.ExceptionAsync(() => session.DisposeAsync().AsTask()))!;
        if (invalidContent)
        {
            Exception[] original = Assert.IsType<AggregateException>(failure).Flatten().InnerExceptions.ToArray();
            Exception[] actual = Assert.IsType<AggregateException>(retired).Flatten().InnerExceptions.ToArray();
            Assert.Equal(original.Length, actual.Length);
            Assert.All(original, cause => Assert.Contains(actual, item => ReferenceEquals(cause, item)));
        }
        else Assert.Same(failure, retired);
        Assert.Same(retired, await Record.ExceptionAsync(() => session.DisposeAsync().AsTask()));
        Assert.Equal(1, access.ReleaseAttempts);
        using MemoryStream retained = Assert.Single(access.Reads);
        Assert.True(retained.CanRead);
        Assert.Equal(1, process.Disposals);
    }

    [Fact]
    public async Task NativeRetirementDrainsAnIgnoringResourceOpenAndReleasesItsActualReader()
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 1, [reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray())], readRelease: release.Task);
        var process = new ManualProcess();
        ILlmAgentSession session = await new ClaudeAgentClient(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        { ModelId = "claude-test", CallerServices = new() { Resources = access }, Runtime = new() { WorkingDirectory = _directory } });
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Read the file")] }).GetAsyncEnumerator();
        Task<bool> read = feed.MoveNextAsync().AsTask();
        try
        {
            await access.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task retirement = session.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, process.Disposals);
            release.TrySetResult();
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.All(access.Reads, stream => Assert.False(stream.CanRead));
            Assert.Single(access.Reads);
            Assert.Equal(1, access.ReleaseAttempts);
            Assert.Equal(1, process.Disposals);
            Assert.DoesNotContain(process.Writes(), item => item.GetProperty("type").GetString() == "user");
        }
        finally
        {
            release.TrySetResult();
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task NativeMcpMaterializesOnlyTheToolsNamedSelectedResourceAndKeepsItsLogicalResult()
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var generated = new InferenceResourceReference("workspace", "generated");
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 4, [generated, reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray()), (generated, "result.json", "{\"answer\":42}"u8.ToArray())]);
        var process = new ManualProcess();
        await using ILlmAgentSession session = await new ClaudeAgentClient(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            CallerServices = new() { Resources = access },
            Tools = [new LlmAgentToolDefinition { Name = "hold", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = (_, _) => Task.FromResult(new LlmToolOutput { Parts = [new LlmToolOutputSessionResource(generated)] })
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<LlmAgentEvent> feed = session.ObserveAsync(limit.Token).GetAsyncEnumerator(limit.Token);
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 7));
        JsonElement answer = await AnswerFor(process, "c1");
        JsonElement resource = Assert.Single(answer.GetProperty("result").GetProperty("content").EnumerateArray()).GetProperty("resource");
        Assert.Equal("application/json", resource.GetProperty("mimeType").GetString());
        Assert.Equal("{\"answer\":42}", resource.GetProperty("text").GetString());
        Assert.Single(access.Reads);
        Assert.False(access.Reads[0].CanRead);
        Assert.Equal(1, access.ReleaseAttempts);
        process.Push(new { type = "result", subtype = "success", is_error = false, result = "answer", stop_reason = "end_turn" });
        List<LlmAgentEvent> events = await ReadUntil(feed, item => item is LlmAgentTurnCompletedEvent);
        LlmAgentToolEvent tool = Assert.Single(events.OfType<LlmAgentToolEvent>(), item => item.Output is not null);
        Assert.Equal(generated, Assert.IsType<LlmToolOutputSessionResource>(Assert.Single(tool.Output!.Parts)).Resource);
        Assert.Equal([generated, reference], access.Selection.SelectedResources);
    }

    [Fact]
    public async Task NativeMcpResourceRefusalReachesTheSessionObserverAndRetirement()
    {
        var generated = new InferenceResourceReference("workspace", "generated");
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 4, [generated]),
            [(generated, "result.pdf", "%PDF-1.7\nactual generated content"u8.ToArray())]);
        var process = new ManualProcess();
        ILlmAgentSession session = await new ClaudeAgentClient(new Factory(process)).StartSessionAsync("connection", Connection(), new()
        {
            ModelId = "claude-test",
            Runtime = new() { WorkingDirectory = _directory },
            CallerServices = new() { Resources = access },
            Tools = [new LlmAgentToolDefinition { Name = "hold", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = (_, _) => Task.FromResult(new LlmToolOutput { Parts = [new LlmToolOutputSessionResource(generated)] })
        });
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        process.Push(ToolUse("tu1"));
        process.Push(ToolCall("c1", 7));
        Exception? observed = await Record.ExceptionAsync(async () =>
        {
            await foreach (LlmAgentEvent _ in session.ObserveAsync(limit.Token)) { }
        });
        Exception? retired = await Record.ExceptionAsync(() => session.DisposeAsync().AsTask());
        var providerFailure = Assert.IsType<LlmProviderException>(observed);
        var resourceFailure = Assert.IsType<InferenceResourceOperationException>(providerFailure.InnerException);
        Assert.IsType<NotSupportedException>(resourceFailure.InnerException);
        Assert.Same(resourceFailure, retired);
        Assert.Same(retired, await Record.ExceptionAsync(() => session.DisposeAsync().AsTask()));
        Assert.Single(access.Reads);
        Assert.False(access.Reads[0].CanRead);
        Assert.Equal(1, access.ReleaseAttempts);
        Assert.DoesNotContain(process.Writes(), item => item.GetProperty("type").GetString() == "control_response");
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
