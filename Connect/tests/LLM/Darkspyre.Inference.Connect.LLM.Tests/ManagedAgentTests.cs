using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ManagedAgentTests
{
    [Fact]
    public async Task UnevaluableToolSchemaRunsTheHostHandler()
    {
        var harness = new Harness();
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null), Text = string.Empty, Status = LlmCompletionStatus.RequiresAction,
            ToolCalls = [new LlmToolCall("call-1", "echo", "{}")]
        });
        harness.Results.Enqueue(new()
        { Diagnostics = new(200, null, null), Text = "tool ran", Status = LlmCompletionStatus.Completed });
        int toolsRun = 0;
        await using var session = harness.Create(Options() with
        {
            Tools = [new LlmAgentToolDefinition
            { Name = "echo", InputSchema = JsonElement.Parse("""{"type":"object","x-provider-note":true}""") }],
            ToolHandler = (_, _) =>
            {
                toolsRun++;
                return Task.FromResult(LlmToolOutput.FromText("ran"));
            }
        });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

        // A schema that cannot be checked does not stop the call.
        Assert.Equal(1, toolsRun);
        Assert.Equal("tool ran", events.OfType<LlmAgentMessageCompletedEvent>().Last().Text);
        Assert.Equal(2, harness.Requests.Count);
        LlmToolResult result = Assert.IsType<LlmToolResult>(
            Assert.Single(harness.Requests[1].Messages.SelectMany(message => message.ToolResults)));
        Assert.False(result.Output.IsError);
    }

    [Fact]
    public async Task UnknownToolTailSurvivesInvalidStructuredOutputWithoutExecutingToolsOrAnotherStep()
    {
        var harness = new Harness();
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null),
            Text = "partial",
            Status = LlmCompletionStatus.Unknown,
            FinishReason = "UNKNOWN_STOP",
            Usage = new(5, 2, 7),
            ToolCalls = [new LlmToolCall("call-1", "echo", "{}")],
            StructuredOutput = new(LlmStructuredOutputEnforcement.ProviderJsonSchema, true, false, ["Incomplete JSON."])
        });
        int toolsRun = 0;
        await using var session = harness.Create(Options() with
        {
            Tools = [new LlmAgentToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = (_, _) =>
            {
                toolsRun++;
                return Task.FromResult(LlmToolOutput.FromText("unexpected"));
            }
        });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

        Assert.Equal(LlmAgentCompletionReason.Unknown, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Equal("partial", Assert.Single(events.OfType<LlmAgentMessageCompletedEvent>()).Text);
        Assert.Equal(new LlmUsage(5, 2, 7), Assert.Single(events.OfType<LlmAgentUsageEvent>()).Usage);
        Assert.Equal(0, toolsRun);
        Assert.Single(harness.Requests);
        LlmAgentContext captured = await session.CaptureContextAsync();
        LlmAgentContext restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(captured, ConnectLlmJson.AgentContext), ConnectLlmJson.AgentContext)!;
        Assert.Equal(["user", "assistant"], restored.Entries.Select(entry => entry.Message.Role));
        Assert.Equal("partial", restored.Entries[1].Message.Content);
        LlmToolCall call = Assert.IsType<LlmToolCall>(Assert.Single(restored.Entries[1].Message.ToolCalls));
        Assert.Equal("call-1", call.Id);
        Assert.Equal("echo", call.Name);
        Assert.Equal("{}", call.ArgumentsJson);
        Assert.Null(restored.Entries[1].Message.ContinuationState);
        Assert.All(restored.Entries, entry => Assert.Empty(entry.Message.ToolResults));
    }

    [Fact]
    public async Task RetirementPreservesEveryParallelToolCleanupFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0;
        Exception[] failures =
        [
            LlmResourcePreparation.AsCleanupFailure(new IOException("The first tool resource could not be released.")),
            LlmResourcePreparation.AsCleanupFailure(new IOException("The second tool resource could not be released."))
        ];
        var harness = new Harness();
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null),
            Text = string.Empty,
            Status = LlmCompletionStatus.RequiresAction,
            ToolCalls = [new LlmToolCall("call-1", "echo", "{}"), new LlmToolCall("call-2", "echo", "{}")]
        });
        var session = harness.Create(Options() with
        {
            Tools = [new LlmAgentToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ExecutionOptions = new() { ToolExecutionMode = LlmAgentToolExecutionMode.Parallel, MaxConcurrentTools = 2 },
            ToolHandler = async (call, _) =>
            {
                if (Interlocked.Increment(ref running) == 2) entered.TrySetResult();
                await release.Task;
                throw failures[call.CallId == "call-1" ? 0 : 1];
            }
        });
        IAsyncEnumerator<LlmAgentEvent> events = session.RunTurnAsync(Turn("hello")).GetAsyncEnumerator();
        try
        {
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task retirement = session.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            release.SetResult();

            Exception? outcome = await Record.ExceptionAsync(() => retirement).WaitAsync(TimeSpan.FromSeconds(5));
            Exception[] causes = Assert.IsType<AggregateException>(outcome).Flatten().InnerExceptions.ToArray();
            Assert.Equal(2, causes.Length);
            Assert.Same(failures[0], causes[0]);
            Assert.Same(failures[1], causes[1]);
            Assert.Same(outcome, await Record.ExceptionAsync(async () => await session.DisposeAsync())
                .WaitAsync(TimeSpan.FromSeconds(5)));
            Exception? readerCleanup = await Record.ExceptionAsync(async () => await events.DisposeAsync())
                .WaitAsync(TimeSpan.FromSeconds(5));
            Exception[] readerCauses = Assert.IsType<AggregateException>(readerCleanup).Flatten().InnerExceptions.ToArray();
            Assert.Equal(2, readerCauses.Length);
            Assert.Same(failures[0], readerCauses[0]);
            Assert.Same(failures[1], readerCauses[1]);
        }
        finally
        {
            release.TrySetResult();
            _ = await Record.ExceptionAsync(async () => await events.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(5));
            _ = await Record.ExceptionAsync(async () => await session.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RetirementDrainsAnIgnoringToolCallbackWithoutWaitingForThePausedEventReader()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LlmToolOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness();
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null),
            Text = string.Empty,
            Status = LlmCompletionStatus.RequiresAction,
            ToolCalls = [new LlmToolCall("call-1", "echo", "{}")]
        });
        await using var session = harness.Create(Options() with
        {
            Tools = [new LlmAgentToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }],
            ToolHandler = async (_, token) =>
            {
                using var registration = token.Register(() => canceled.TrySetResult());
                entered.TrySetResult();
                return await release.Task;
            }
        });
        await using IAsyncEnumerator<LlmAgentEvent> events = session.RunTurnAsync(Turn("hello")).GetAsyncEnumerator();
        try
        {
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsType<LlmAgentTurnStartedEvent>(events.Current);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task retirement = session.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(retirement.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CaptureContextAsync());

            release.SetResult(LlmToolOutput.FromText("settled"));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(harness.Requests);
        }
        finally
        {
            release.TrySetResult(LlmToolOutput.FromText("settled"));
        }
    }

    [Fact]
    public async Task HostToolSchemasAreSentUnchanged()
    {
        // An optional property without additionalProperties is a valid host schema but not a strict one.
        JsonElement loose = JsonElement.Parse(
            """{"type":"object","properties":{"command":{"type":"string"},"timeout":{"type":"integer"}},"required":["command"]}""");
        var harness = new Harness();
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "done", Status = LlmCompletionStatus.Completed });
        await using var session = harness.Create(ToolsOptions(_ => new()) with
        {
            Tools = [new LlmAgentToolDefinition() { Name = "bash", InputSchema = loose }, new LlmAgentToolDefinition() { Name = "echo", InputSchema = loose }]
        });
        await Read(session.RunTurnAsync(Turn("hello")));
        LlmToolDefinition[] sent = [.. harness.Requests.Single().Tools.Select(tool => Assert.IsType<LlmToolDefinition>(tool))];
        Assert.Equal(["bash", "echo"], sent.Select(tool => tool.Name));
        Assert.Equal(loose.GetRawText(), sent[0].InputSchema.GetRawText()); // The schema itself is sent unchanged.
    }

    [Fact]
    public async Task TerminalUsageReplacesCumulativeObservationsAcrossToolContinuation()
    {
        var harness = new Harness();
        harness.UsageObservations.Enqueue([new(8, 1, 9), new(8, 2, 10)]);
        harness.UsageObservations.Enqueue([new(10, 4, 14)]);
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null),
            Text = string.Empty,
            Status = LlmCompletionStatus.RequiresAction,
            Usage = new(8, 3, 11) { CachedInputTokens = 6 },
            ToolCalls = [new LlmToolCall("call-1", "echo", "{}")]
        });
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null),
            Text = "done",
            Status = LlmCompletionStatus.Completed,
            Usage = new(10, 2, 12)
        });
        await using var session = harness.Create(Options() with
        {
            Tools = [new LlmAgentToolDefinition
            {
                Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\"}")
            }],
            ToolHandler = (call, _) => Task.FromResult(LlmToolOutput.FromText("echoed " + call.CallId))
        });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));
        LlmAgentUsageEvent[] observations = [.. events.OfType<LlmAgentUsageEvent>()];
        Assert.Equal(5, observations.Length);
        Assert.Equal([9, 10, 11, 14, 12], observations.Select(item => item.Usage!.TotalTokens));
        Assert.Equal([9, 10, 11, 25, 23], observations.Select(item => item.RunTotals!.TotalTokens));
        Assert.Equal([observations[0].RequestId, observations[0].RequestId, observations[0].RequestId,
            observations[3].RequestId, observations[3].RequestId], observations.Select(item => item.RequestId));
        Assert.NotEqual(observations[0].RequestId, observations[3].RequestId);
        Assert.All(observations, item =>
        {
            Assert.Equal(session.ThreadId, item.ThreadId);
            Assert.Equal(observations[0].TurnId, item.TurnId);
        });
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);

        LlmUsage total = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(observations[^1].RunTotals!, InferenceJson.LlmUsage), InferenceJson.LlmUsage)!;
        Assert.Equal(new LlmUsage(18, 5, 23), total);
        Assert.Null(total.CachedInputTokens);
        Assert.Null(total.ProviderReportedCostUsd);

        LlmAgentContext capture = await session.CaptureContextAsync(new() { IncludeAttachmentData = true });
        LlmAgentContext restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(capture, ConnectLlmJson.AgentContext), ConnectLlmJson.AgentContext)!;
        Assert.Equal(["user", "assistant", "user", "assistant"], restored.Entries.Select(entry => entry.Message.Role));
        LlmToolCall call = Assert.IsType<LlmToolCall>(Assert.Single(restored.Entries[1].Message.ToolCalls));
        Assert.Equal("call-1", call.Id);
        LlmToolResult toolResult = Assert.IsType<LlmToolResult>(Assert.Single(restored.Entries[2].Message.ToolResults));
        Assert.Equal("call-1", toolResult.ToolCallId);
        Assert.Equal("echoed call-1", Assert.IsType<LlmToolOutputText>(Assert.Single(toolResult.Output.Parts)).Text);
        Assert.Equal("done", restored.Entries[^1].Message.Content);
        Assert.Equal(2, harness.Requests.Count);
    }

    /// <summary>A disconnected step retains completed tools and retries the same committed history.</summary>
    [Fact]
    public async Task InterruptedStepRecoversWithoutReplayingCompletedTools()
    {
        var harness = new Harness { RequestFailure = number => number == 2 ? Interrupted() : null, ExposeBeforeFailure = true };
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null), Status = LlmCompletionStatus.Completed,
            ToolCalls = [new LlmToolCall("call-1", "echo", "{}")], Text = "tool step"
        });
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Status = LlmCompletionStatus.Completed, Text = "recovered" });
        int calls = 0;
        await using var session = harness.Create(ToolsOptions(_ => { calls++; return LlmToolOutput.FromText("retained result"); }));

        var events = await Read(session.RunTurnAsync(Turn("hello")));

        Assert.Equal(1, calls);
        Assert.Equal(3, harness.Requests.Count);
        Assert.Same(harness.Requests[1], harness.Requests[2]);
        Assert.Contains(harness.Requests[2].Messages.SelectMany(message => message.ToolResults),
            result => result is LlmToolResult { ToolCallId: "call-1" });
        var recovery = Assert.Single(events.OfType<LlmAgentResponseRecoveryEvent>());
        Assert.Equal(2, recovery.NextAttempt);
        Assert.Contains(events.OfType<LlmAgentTextDeltaEvent>(), item => item.ItemId == recovery.ItemId);
        Assert.Contains(events.OfType<LlmAgentActivityEvent>(), item => item.ItemId != recovery.ItemId && item.Activity == string.Empty);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        var captured = await session.CaptureContextAsync();
        Assert.DoesNotContain(captured.Entries, entry => entry.Message.Content == "partial");
        Assert.Equal("recovered", captured.Entries[^1].Message.Content);
    }

    /// <summary>Three interrupted attempts terminate without admitting unfinished output.</summary>
    [Fact]
    public async Task InterruptedStepExhaustsThreeAttempts()
    {
        var harness = new Harness { Failure = Interrupted(), ExposeBeforeFailure = true };
        await using var session = harness.Create();
        var events = await Read(session.RunTurnAsync(Turn("hello")));
        Assert.Equal(3, harness.Requests.Count);
        var resets = events.OfType<LlmAgentResponseRecoveryEvent>().ToArray();
        Assert.Equal(new int?[] { 2, 3, null }, resets.Select(item => item.NextAttempt));
        Assert.Equal(3, resets.Select(item => item.ItemId).Distinct().Count());
        var completed = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, completed.Reason);
        Assert.Equal("The model response stream was interrupted. Recovery failed after 3 attempts. Completed tool results are retained. Resume to continue.", completed.Error);
        Assert.Empty(events.OfType<LlmAgentMessageCompletedEvent>());
    }

    /// <summary>A hosted-tool request is never replayed even without observed hosted activity.</summary>
    [Fact]
    public async Task InterruptedHostedToolRequestDoesNotReplay()
    {
        var harness = new Harness { Failure = Interrupted() };
        await using var session = harness.Create(Options() with { HostedTools = [new LlmWebSearchTool()] });
        var events = await Read(session.RunTurnAsync(Turn("hello")));
        Assert.Single(harness.Requests);
        Assert.Null(Assert.Single(events.OfType<LlmAgentResponseRecoveryEvent>()).NextAttempt);
        Assert.Contains("Provider-side work may already have run", Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Error);
    }

    /// <summary>A rejected request is not an interrupted response body and does not retry.</summary>
    [Fact]
    public async Task ProviderRejectionDoesNotRetry()
    {
        var harness = new Harness { ExposeBeforeFailure = true, Failure = new LlmProviderException("Provider overloaded.", LlmTransport.OpenAiCodex, HttpStatusCode.ServiceUnavailable) };
        await using var session = harness.Create();
        var events = await Read(session.RunTurnAsync(Turn("hello")));
        Assert.Single(harness.Requests);
        Assert.Empty(events.OfType<LlmAgentResponseRecoveryEvent>());
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
    }

    /// <summary>Stop during the retry wait prevents a replacement request.</summary>
    [Fact]
    public async Task CancellationDuringRecoveryWaitDoesNotSendAnotherRequest()
    {
        var harness = new Harness { Failure = Interrupted() };
        await using var session = harness.Create();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in session.RunTurnAsync(Turn("hello"), cancellation.Token))
                if (item is LlmAgentResponseRecoveryEvent) cancellation.Cancel();
        });
        Assert.Single(harness.Requests);
    }

    private static LlmChatResult Done(string text = "done", string? modelId = null) =>
        new() { Diagnostics = new(200, null, null), Text = text, Status = LlmCompletionStatus.Completed, ModelId = modelId };

    private static LlmProviderCapabilities Capable(InferenceSupport support, LlmOutputLimitEnforcement output) => new()
    {
        SupportsAgentSessions = InferenceSupport.Supported,
        SupportsAgentToolUse = InferenceSupport.Supported,
        SupportsToolUse = InferenceSupport.Supported,
        SupportsTemperature = support,
        SupportsPromptImages = support,
        OutputLimitEnforcement = output,
        RichContentSupported = RichContentRoutes.Responses,
        Agent = ManagedAgentPolicy.Capabilities
    };

    [Fact]
    public async Task UnusableSettingsAreDropped()
    {
        var harness = new Harness { Capabilities = Capable(InferenceSupport.Unsupported, LlmOutputLimitEnforcement.Unsupported) };
        harness.Results.Enqueue(Done());
        await using var session = harness.Create();

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello") with
        {
            Temperature = 0.5,
            MaxOutputTokens = 100,
            ToolChoice = new() { Mode = LlmToolChoiceMode.Required }
        }));

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        LlmChatRequest sent = Assert.Single(harness.Requests);
        Assert.Null(sent.Temperature);
        Assert.Null(sent.MaxOutputTokens);
        Assert.Null(sent.ToolChoice); // The session has no tools.
    }

    [Fact]
    public async Task TurnSettingsReachTheRequest()
    {
        var harness = new Harness { Capabilities = Capable(InferenceSupport.Supported, LlmOutputLimitEnforcement.ProviderEnforced) };
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null), Text = string.Empty, Status = LlmCompletionStatus.RequiresAction,
            ToolCalls = [new LlmToolCall("call-1", "echo", "{}")]
        });
        harness.Results.Enqueue(Done());
        await using var session = harness.Create(ToolsOptions(_ => LlmToolOutput.FromText("echoed")));

        await Read(session.RunTurnAsync(Turn("hello") with
        {
            Temperature = 0.3,
            MaxOutputTokens = 50,
            ToolChoice = new() { Mode = LlmToolChoiceMode.Specific, Name = "echo" }
        }));

        Assert.Equal(2, harness.Requests.Count);
        Assert.All(harness.Requests, request => Assert.Equal((0.3, 50), (request.Temperature, request.MaxOutputTokens)));
        Assert.Equal(new LlmToolChoice { Mode = LlmToolChoiceMode.Specific, Name = "echo" }, harness.Requests[0].ToolChoice);
        // The choice applies to the first step only, so the model is not forced to call the tool again.
        Assert.Equal(new LlmToolChoice { Mode = LlmToolChoiceMode.Auto }, harness.Requests[1].ToolChoice);
    }

    [Fact]
    public async Task UnsendableAttachmentIsOmittedWithNotice()
    {
        var harness = new Harness { Capabilities = Capable(InferenceSupport.Unsupported, LlmOutputLimitEnforcement.Unknown) };
        harness.Results.Enqueue(Done());
        await using var session = harness.Create();
        var image = new LlmAttachment
        { Kind = LlmBinaryKind.Image, FileName = "chart.png", Data = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a } };

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("Look") with { Attachments = [image] }));

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        LlmChatMessage sent = Assert.Single(harness.Requests).Messages[^1];
        Assert.Empty(sent.Attachments);
        Assert.Equal("Look\n[Attachment 0 (chart.png): The selected model does not support this attachment. "
            + "This content was not supplied to the model.]", sent.Content);
        LlmChatMessage stored = (await session.CaptureContextAsync(new() { IncludeAttachmentData = true })).Entries[0].Message;
        Assert.Equal("Look", stored.Content);
        Assert.Equal("chart.png", Assert.IsType<LlmAttachment>(Assert.Single(stored.Attachments)).FileName);
    }

    [Fact]
    public async Task UnsupportedOperationIsRefusedByName()
    {
        await using var providers = new LlmAgentProviderService(new ResourcePreparationTests.Source(Harness.Connection), [new CodexClient()]);

        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(
            () => providers.ResumeSessionAsync("connection", "thread"));

        Assert.Equal("OpenAiCodex agent sessions cannot resume a provider thread.", error.Message);
    }

    [Fact]
    public async Task ForeignHistoryContinuesOnStart()
    {
        // State another model wrote cannot be replayed here; it is left out and counted, and the turn runs.
        LlmContinuationState foreign = ProviderContinuationStateCodec.Encode(Harness.Connection, "removed-model",
            new JsonArray(new JsonObject { ["type"] = "reasoning", ["encrypted_content"] = "opaque" }));
        var harness = new Harness();
        harness.Results.Enqueue(Done());
        await using var session = harness.Create(Options() with
        {
            Context = new()
            {
                Entries = [new("e1", "t1", new("user", "earlier")), new("e2", "t1", new("assistant", "answer") { ContinuationState = foreign })]
            }
        });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("next")));

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Equal(["earlier", "answer", "next"], Assert.Single(harness.Requests).Messages.Select(message => message.Content));
        Assert.All(harness.Requests[0].Messages, message => Assert.Null(message.ContinuationState));
        Assert.Contains(events.OfType<LlmAgentWarningEvent>(), warning => warning.Message.Contains("from 1 earlier messages", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyInputSendsContextAsIs()
    {
        var harness = new Harness();
        harness.Results.Enqueue(Done());
        await using var session = harness.Create(Options() with
        {
            Context = new() { Entries = [new("e1", "t1", new("user", "question"))] }
        });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(new()));

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Equal(["question"], Assert.Single(harness.Requests).Messages.Select(message => message.Content));

        await using var empty = new Harness().Create();
        Assert.Equal(LlmAgentCompletionReason.Completed,
            Assert.Single((await Read(empty.RunTurnAsync(new()))).OfType<LlmAgentTurnCompletedEvent>()).Reason);
    }

    [Fact]
    public async Task MalformedContextIsRepairedNotRefused()
    {
        LlmAgentContextEntry[] entries =
        [
            new("e1", "t1", new("user", "first")),
            new("e1", "t1", new("user", "duplicate")),
            new("e2", "t1", new("user", "") { ToolResults = [new LlmToolResult("orphan", LlmToolOutput.FromText("lost"))] }),
            new("e3", "t1", new("assistant", "") { ToolCalls = [new LlmToolCall("call-1", "echo", "{}")] })
        ];
        var harness = new Harness();
        harness.Results.Enqueue(Done());
        await using var session = harness.Create(Options() with { Context = new() { Entries = entries } });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(new()));

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        IReadOnlyList<LlmChatMessage> sent = Assert.Single(harness.Requests).Messages;
        // The duplicate entry and the result with no call are not sent; the unsettled call gets an error result.
        Assert.Equal([("user", 0, 0), ("assistant", 1, 0), ("user", 0, 1)],
            sent.Select(message => (message.Role, message.ToolCalls.Count, message.ToolResults.Count)));
        Assert.Equal("first", sent[0].Content);
        LlmToolResult settled = Assert.IsType<LlmToolResult>(Assert.Single(sent[2].ToolResults));
        Assert.Equal("call-1", settled.ToolCallId);
        Assert.Equal("The tool was not run; no result is available.", Assert.IsType<LlmToolOutputText>(Assert.Single(settled.Output.Parts)).Text);
        Assert.Equal(["first", "duplicate", "", "", "done"],
            (await session.CaptureContextAsync()).Entries.Select(entry => entry.Message.Content));
    }

    [Fact]
    public async Task UsageReportsTheModel()
    {
        var harness = new Harness();
        harness.UsageObservations.Enqueue([new(3, 1, 4)]);
        harness.Results.Enqueue(Done(modelId: "answering-model") with { Usage = new(3, 2, 5) });
        await using var session = harness.Create();

        LlmAgentUsageEvent[] usage = [.. (await Read(session.RunTurnAsync(Turn("hello")))).OfType<LlmAgentUsageEvent>()];

        Assert.Equal(["test", "answering-model"], usage.Select(item => item.ModelId));
    }

    private static LlmChatResult Calls(params LlmToolCall[] calls) => new()
    {
        Diagnostics = new(200, null, null), Text = string.Empty, Status = LlmCompletionStatus.RequiresAction, ToolCalls = calls
    };

    private static LlmAgentToolDefinition Lookup() => new()
    {
        Name = "lookup",
        InputSchema = JsonElement.Parse(
            """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false}""")
    };

    [Fact]
    public async Task InvalidArgumentsReturnToTheModel()
    {
        var harness = new Harness();
        harness.Results.Enqueue(Calls(new LlmToolCall("missing", "lookup", """{"town":"Paris"}"""),
            new LlmToolCall("broken", "lookup", "not json")));
        harness.Results.Enqueue(Done());
        int toolsRun = 0;
        await using var session = harness.Create(Options() with
        {
            Tools = [Lookup()],
            ToolHandler = (_, _) => { toolsRun++; return Task.FromResult(LlmToolOutput.FromText("unexpected")); }
        });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

        Assert.Equal(0, toolsRun);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Equal(2, harness.Requests.Count);
        LlmToolResult[] results = [.. harness.Requests[1].Messages.SelectMany(message => message.ToolResults).OfType<LlmToolResult>()];
        string missing = Assert.IsType<LlmToolOutputText>(Assert.Single(results.Single(result => result.ToolCallId == "missing").Output.Parts)).Text;
        Assert.StartsWith("Tool 'lookup' arguments were rejected:\n", missing, StringComparison.Ordinal);
        Assert.Contains("city", missing, StringComparison.Ordinal);
        Assert.All(results, result => Assert.True(result.Output.IsError));
        LlmAgentToolEvent[] failed = [.. events.OfType<LlmAgentToolEvent>()];
        Assert.All(failed, item => Assert.Equal("failed", item.Status));
        Assert.Equal("Paris", failed.Single(item => item.ItemId == "missing").Arguments.GetProperty("town").GetString());
        Assert.Equal("not json", failed.Single(item => item.ItemId == "broken").Arguments.GetString());
    }

    [Fact]
    public async Task SuperfluousArgumentsAreIgnored()
    {
        const string sent = """{"city":"Paris","units":"metric"}""";
        var harness = new Harness();
        harness.Results.Enqueue(Calls(new LlmToolCall("call-1", "lookup", sent)));
        harness.Results.Enqueue(Done());
        JsonElement? received = null;
        await using var session = harness.Create(Options() with
        {
            Tools = [Lookup()],
            ToolHandler = (call, _) => { received = call.Arguments.Clone(); return Task.FromResult(LlmToolOutput.FromText("sunny")); }
        });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

        Assert.Equal(sent, received!.Value.GetRawText());
        Assert.Equal("completed", events.OfType<LlmAgentToolEvent>().Last().Status);
        LlmToolResult result = Assert.IsType<LlmToolResult>(Assert.Single(harness.Requests[1].Messages.SelectMany(message => message.ToolResults)));
        Assert.False(result.Output.IsError);
    }

    [Fact]
    public async Task ReturnRequestsEndsTurnAtSettledBoundary()
    {
        var harness = new Harness();
        harness.Results.Enqueue(Calls(new LlmToolCall("good", "lookup", """{"city":"Paris"}"""),
            new LlmToolCall("bad", "lookup", "{}")) with { Text = "checking" });
        harness.Results.Enqueue(Done());
        // No handler: the calls are returned, not run.
        await using var session = harness.Create(Options() with
        {
            Tools = [Lookup()],
            ToolHandling = LlmAgentToolHandling.ReturnRequests
        });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

        Assert.Single(harness.Requests);
        LlmAgentTurnCompletedEvent completed = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(LlmAgentCompletionReason.ToolRequests, completed.Reason);
        Assert.Null(completed.Error);
        LlmAgentToolEvent good = events.OfType<LlmAgentToolEvent>().Single(item => item.ItemId == "good");
        Assert.Equal(("returned", (bool?)null, (LlmToolOutput?)null), (good.Status, good.Success, good.Output));
        Assert.Equal("Paris", good.Arguments.GetProperty("city").GetString());
        LlmAgentToolEvent bad = events.OfType<LlmAgentToolEvent>().Single(item => item.ItemId == "bad");
        Assert.Equal(("failed", (bool?)false), (bad.Status, bad.Success));
        Assert.StartsWith("Tool 'lookup' arguments were rejected:", Assert.IsType<LlmToolOutputText>(Assert.Single(bad.Output!.Parts)).Text,
            StringComparison.Ordinal);
        LlmChatMessage assistant = (await session.CaptureContextAsync()).Entries[^1].Message;
        Assert.Equal(("assistant", "checking", 0), (assistant.Role, assistant.Content, assistant.ToolCalls.Count));
        Assert.Null(assistant.ContinuationState);

        await Read(session.RunTurnAsync(Turn("next")));

        Assert.All(harness.Requests[1].Messages, message =>
            Assert.Equal((0, 0), (message.ToolCalls.Count, message.ToolResults.Count)));
    }

    [Fact]
    public async Task HandlerFailureIsRedacted()
    {
        const string secret = "sk-test-redaction-value";
        const string header = "header-redaction-value";
        var harness = new Harness
        {
            SessionConnection = Harness.Connection with
            {
                Credential = new LlmCredential { ApiKey = secret }.Serialize(),
                AdditionalHeaders = new Dictionary<string, string> { ["x-tenant"] = header }
            }
        };
        harness.Results.Enqueue(Calls(new LlmToolCall("call-1", "lookup", """{"city":"Paris"}""")));
        harness.Results.Enqueue(Done());
        await using var session = harness.Create(Options() with
        {
            Tools = [Lookup()],
            ToolHandler = (_, _) => throw new InvalidOperationException($"Upstream refused {secret} for {header}.")
        });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

        LlmToolResult result = Assert.IsType<LlmToolResult>(Assert.Single(harness.Requests[1].Messages.SelectMany(message => message.ToolResults)));
        string sent = Assert.IsType<LlmToolOutputText>(Assert.Single(result.Output.Parts)).Text;
        string reported = Assert.IsType<LlmToolOutputText>(Assert.Single(events.OfType<LlmAgentToolEvent>().Last().Output!.Parts)).Text;
        Assert.Equal(sent, reported);
        Assert.StartsWith("InvalidOperationException: Upstream refused ", sent, StringComparison.Ordinal);
        Assert.EndsWith(" External outcome is unknown. Do not automatically replay.", sent, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, sent, StringComparison.Ordinal);
        Assert.DoesNotContain(header, sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingHandlerGivesEachCallAnErrorResult()
    {
        var harness = new Harness();
        harness.Results.Enqueue(Calls(new LlmToolCall("call-1", "lookup", """{"city":"Paris"}""")));
        harness.Results.Enqueue(Done());
        await using var session = harness.Create(Options() with { Tools = [Lookup()] });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        LlmToolResult result = Assert.IsType<LlmToolResult>(Assert.Single(harness.Requests[1].Messages.SelectMany(message => message.ToolResults)));
        Assert.True(result.Output.IsError);
        Assert.Equal("No handler is available for tool 'lookup'.", Assert.IsType<LlmToolOutputText>(Assert.Single(result.Output.Parts)).Text);
    }

    [Fact]
    public async Task NonObjectSchemaIsWrappedAndValueUnwrapped()
    {
        JsonElement schema = JsonElement.Parse("""{"type":"string","minLength":2}""");
        var harness = new Harness();
        harness.Results.Enqueue(Calls(new LlmToolCall("good", "say", """{"value":"hi"}"""),
            new LlmToolCall("bad", "say", """{"value":"h"}""")));
        harness.Results.Enqueue(Done());
        var received = new List<string>();
        await using var session = harness.Create(Options() with
        {
            Tools = [new LlmAgentToolDefinition { Name = "say", InputSchema = schema }],
            ToolHandler = (call, _) => { lock (received) received.Add(call.Arguments.GetRawText()); return Task.FromResult(LlmToolOutput.FromText("ok")); }
        });

        await Read(session.RunTurnAsync(Turn("hello")));

        LlmToolDefinition sent = Assert.IsType<LlmToolDefinition>(Assert.Single(harness.Requests[0].Tools));
        Assert.Equal("""{"type":"object","properties":{"value":{"type":"string","minLength":2}},"required":["value"]}""",
            sent.InputSchema.GetRawText());
        Assert.Equal(["\"hi\""], received);
        LlmToolResult[] results = [.. harness.Requests[1].Messages.SelectMany(message => message.ToolResults).OfType<LlmToolResult>()];
        Assert.True(results.Single(result => result.ToolCallId == "bad").Output.IsError);
    }

    [Fact]
    public async Task CodexCompactsAtTriggerToTarget()
    {
        // A 10,000-token window with a 100-token reserve: trigger 8,415, target 3,465.
        var harness = new Harness { Window = 10000 };
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null), Text = string.Empty, Status = LlmCompletionStatus.RequiresAction,
            ToolCalls = [new LlmToolCall("call-1", "echo", "{}")]
        });
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "done", Status = LlmCompletionStatus.Completed });
        await using var session = harness.Create(ToolsOptions(_ => LlmToolOutput.FromText(new string('x', 9000))));

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

        Assert.Empty(events.OfType<LlmAgentCompactionRequiredEvent>());
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        // The first step measured below the trigger and did not compact; the second compacted by shortening alone.
        LlmAgentCompactedEvent compacted = Assert.Single(events.OfType<LlmAgentCompactedEvent>());
        Assert.Null(compacted.DroppedMessageCount);
        Assert.Equal(1, compacted.ShortenedToolOutputCount);
        // Each decision point reports its measure: step one, step two before and after compaction.
        Assert.Equal(new bool?[] { false, true, false }, events.OfType<LlmAgentContextStatusEvent>().Select(status => status.Status.NeedsCompaction));
        Assert.Empty(harness.Summaries);
        Assert.Equal(2, harness.Requests.Count);
        LlmToolResult sent = Assert.IsType<LlmToolResult>(
            Assert.Single(harness.Requests[1].Messages.SelectMany(message => message.ToolResults)));
        Assert.Contains("of 9000 characters removed by compaction.]", Assert.IsType<LlmToolOutputText>(Assert.Single(sent.Output.Parts)).Text);
        Assert.InRange(harness.EstimateInputTokens(Harness.Connection, harness.Requests[1], ResourceSizes.None)!.Value.Tokens, 0, 3465);
    }

    [Fact]
    public async Task CodexRefusalCompactsAndResendsOnce()
    {
        static LlmProviderException TooLong() => new("Your input exceeds the context window of this model.",
            LlmTransport.OpenAiCodex, isContextOverflow: true);
        // The first refusal is compacted and the step sent again; the Host never sees it.
        var recovered = new Harness { RequestFailure = request => request == 1 ? TooLong() : null, SummaryText = "summary of turn one" };
        recovered.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "answer", Status = LlmCompletionStatus.Completed });
        await using (var session = recovered.Create(Options() with { Context = History(("t1", new string('a', 600), "first answer")) }))
        {
            List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

            Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
            Assert.Single(events.OfType<LlmAgentCompactedEvent>());
            Assert.Equal(2, recovered.Requests.Count);
            string resent = string.Join("\n", recovered.Requests[1].Messages.Select(message => message.Content));
            Assert.Contains("summary of turn one", resent);
            Assert.DoesNotContain(new string('a', 600), resent);
        }

        // A second refusal in the same turn ends it with the provider's message and is not sent again.
        var refused = new Harness { RequestFailure = _ => TooLong() };
        await using (var session = refused.Create(Options() with { Context = History(("t1", new string('a', 600), "first answer")) }))
        {
            List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

            LlmAgentTurnCompletedEvent completed = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
            Assert.Equal(LlmAgentCompletionReason.ProviderFailure, completed.Reason);
            Assert.Equal("Your input exceeds the context window of this model.", completed.Error);
            Assert.Equal(2, refused.Requests.Count);
        }
    }

    [Fact]
    public async Task PreservedContextNeverCompacts()
    {
        var harness = new Harness
        {
            RequestFailure = _ => new LlmProviderException("Prompt is too long.", LlmTransport.OpenAiCodex, isContextOverflow: true)
        };
        await using var session = harness.Create(Options() with
        {
            Context = History(("t1", new string('a', 800), "first answer")),
            ExecutionOptions = new() { ContextPolicy = new() { PreserveSuppliedContext = true } }
        });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

        Assert.True(Assert.Single(events.OfType<LlmAgentContextStatusEvent>()).Status.NeedsCompaction);
        Assert.Empty(events.OfType<LlmAgentCompactedEvent>());
        LlmAgentTurnCompletedEvent completed = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, completed.Reason);
        Assert.Equal("Prompt is too long.", completed.Error);
        Assert.Contains(new string('a', 800), Assert.Single(harness.Requests).Messages.Select(message => message.Content));
    }

    [Fact]
    public async Task FailedSummaryReportsErrorAndKeepsContext()
    {
        var harness = new Harness
        {
            SummaryFailure = new LlmProviderException("The summary model is overloaded.", LlmTransport.OpenAiCodex)
            { Usage = new(10, 2, 12) }
        };
        LlmAgentContext history = History(("t1", new string('a', 800), "first answer"));
        await using var session = harness.Create(Options() with { Context = history });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

        LlmAgentTurnCompletedEvent completed = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, completed.Reason);
        Assert.Equal("The summary model is overloaded.", completed.Error);
        LlmAgentUsageEvent usage = Assert.Single(events.OfType<LlmAgentUsageEvent>());
        Assert.Equal(new LlmUsage(10, 2, 12), usage.Usage);
        Assert.True(events.IndexOf(usage) < events.IndexOf(completed));
        Assert.Empty(events.OfType<LlmAgentCompactedEvent>());
        Assert.Single(harness.Summaries);
        Assert.Empty(harness.Requests);
        LlmAgentContext kept = await session.CaptureContextAsync();
        Assert.Equal([.. history.Entries.Select(entry => entry.Message.Content), "hello"],
            kept.Entries.Select(entry => entry.Message.Content));
    }

    [Fact]
    public async Task OmittedNativeCompactionIsSummarizedBeforeFirstSend()
    {
        // A Codex session another connection compacted natively: one entry holds only the compaction state, and the
        // turns it replaced exist only in the Host's transcript.
        var foreignState = new LlmContinuationState
        { ImplementationId = "openai-codex", ModelId = "other", ScopeId = "other-account", Payload = "opaque" };
        LlmAgentContext recent = History(("t2", "recent question", "recent answer"));
        var context = recent with
        {
            Entries = [new("native", "native", new("assistant", string.Empty) { ContinuationState = foreignState }), .. recent.Entries]
        };
        var transcript = new Transcript([.. History(("t0", "oldest question", "oldest answer"),
            ("t1", "older question", "older answer")).Entries, .. recent.Entries]);
        var harness = new Harness { SummaryText = "summary of t0 and t1" };
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "answer", Status = LlmCompletionStatus.Completed });
        await using var session = harness.Create(Options() with
        {
            Context = context,
            CallerServices = new() { Transcript = transcript }
        });
        Assert.Empty(harness.Summaries);

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("next")));

        Assert.Equal(1, Assert.Single(events.OfType<LlmAgentCompactedEvent>()).DroppedMessageCount);
        string summarized = Assert.Single(Assert.Single(harness.Summaries).Messages).Content;
        Assert.Contains("oldest question", summarized);
        Assert.Contains("older answer", summarized);
        Assert.DoesNotContain("recent question", summarized);
        Assert.Equal(["Summary of the earlier conversation:\nsummary of t0 and t1", "recent question", "recent answer", "next"],
            Assert.Single(harness.Requests).Messages.Select(message => message.Content));
    }

    [Fact]
    public async Task CompactNowSummarizesAnIdleConversation()
    {
        var harness = new Harness { SummaryText = "summary of t1" };
        LlmAgentContext history = History(("t1", new string('a', 400), "first answer"), ("t2", new string('b', 300), "second answer"));
        await using var session = harness.Create(Options() with { Context = history });

        LlmAgentCompactedEvent? compacted = await session.CompactNowAsync();

        Assert.NotNull(compacted);
        Assert.Equal(2, compacted.DroppedMessageCount);
        Assert.Equal("t2", compacted.TurnId);
        LlmAgentContext result = await session.CaptureContextAsync();
        Assert.Equal(compacted.ContextRevision, result.Revision);
        Assert.Equal(["Summary of the earlier conversation:\nsummary of t1", new string('b', 300), "second answer"],
            result.Entries.Select(entry => entry.Message.Content));

        // A running session refuses instead of compacting.
        var running = new Harness();
        running.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "answer", Status = LlmCompletionStatus.Completed });
        await using var busy = running.Create(Options() with { Context = history });
        await using IAsyncEnumerator<LlmAgentEvent> turn = busy.RunTurnAsync(Turn("hello")).GetAsyncEnumerator();
        Assert.True(await turn.MoveNextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => busy.CompactNowAsync());
        while (await turn.MoveNextAsync()) { }
        Assert.Single(running.Requests);
    }

    /// <summary>A context of whole turns, each a user message and the assistant's answer.</summary>
    private static LlmAgentContext History(params (string TurnId, string Question, string Answer)[] turns) => new()
    {
        Entries = [.. turns.SelectMany(turn => new LlmAgentContextEntry[]
        {
            new(turn.TurnId + "-user", turn.TurnId, new("user", turn.Question)),
            new(turn.TurnId + "-assistant", turn.TurnId, new("assistant", turn.Answer))
        })]
    };

    private sealed class Transcript(IReadOnlyList<LlmAgentContextEntry> entries) : ILlmConversationTranscript
    {
        public Task<LlmTranscriptPage> ReadAsync(string? cursor, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmTranscriptPage(entries, null));
    }

    private sealed class CodexClient : ILlmAgentClient
    {
        public LlmTransport Transport => LlmTransport.OpenAiCodex;
        public Task<ILlmAgentSession> StartSessionAsync(string connectionId, LlmConnection connection,
            LlmAgentSessionOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static LlmProviderException Interrupted() => new("The model response stream was interrupted.",
        LlmTransport.OpenAiCodex, isResponseStreamInterrupted: true);

    private static LlmAgentTurnRequest Turn(string text) => new() { Input = [new LlmAgentTextContent(text)] };
    private static LlmAgentSessionOptions Options() => new() { ModelId = "test" };
    private static LlmAgentSessionOptions ToolsOptions(Func<LlmAgentToolCall, LlmToolOutput> handler) => Options() with
    {
        Tools = [new LlmAgentToolDefinition() { Name = "echo", InputSchema = JsonElement.Parse(
            """{"type":"object","properties":{},"additionalProperties":false}""") }],
        ToolHandler = (call, _) => Task.FromResult(handler(call))
    };
    private static async Task<List<LlmAgentEvent>> Read(IAsyncEnumerable<LlmAgentEvent> source)
    {
        var events = new List<LlmAgentEvent>();
        await foreach (var item in source) { events.Add(item); }
        return events;
    }

    private sealed class Harness : ILlmContextEstimator
    {
        public Queue<LlmChatResult> Results { get; } = new();
        public Queue<IReadOnlyList<LlmUsage>> UsageObservations { get; } = new();
        public List<LlmChatRequest> Requests { get; } = [];
        public List<LlmChatRequest> Estimates { get; } = [];
        /// <summary>Compaction summary requests, answered with <see cref="SummaryText"/> or <see cref="SummaryFailure"/>.</summary>
        public List<LlmChatRequest> Summaries { get; } = [];
        public string SummaryText { get; init; } = "summary";
        public Exception? SummaryFailure { get; init; }
        public Exception? Failure { get; set; }
        /// <summary>An optional scripted failure for each numbered model request.</summary>
        public Func<int, Exception?>? RequestFailure { get; init; }
        public IReadOnlyList<JsonElement> Snapshots { get; init; } = [];
        public bool UnknownEstimate { get; init; }
        public bool ExposeBeforeFailure { get; init; }
        /// <summary>The provider capabilities the session sees; null uses the default fixture.</summary>
        public LlmProviderCapabilities? Capabilities { get; init; }
        public static LlmConnection Connection { get; } =
            new() { Version = "1", Transport = LlmTransport.OpenAiCodex, Endpoint = "https://provider.test/codex" };
        /// <summary>The connection the session uses; <see cref="Connection"/> unless a test sets one.</summary>
        public LlmConnection SessionConnection { get; init; } = Connection;
        public ManagedAgentSession Create(LlmAgentSessionOptions? options = null) => ManagedAgentSession.CreateAsync(
            SessionConnection,
            options ?? Options(), [new("test", "Test") { MaxContextTokens = Window, MaxOutputTokens = 100 }, new("other", "Other") { MaxContextTokens = 2000, MaxOutputTokens = 100 }], this, Stream,
            _ => Capabilities ?? new()
            {
                SupportsAgentSessions = InferenceSupport.Supported,
                SupportsAgentToolUse = InferenceSupport.Supported,
                SupportsAgentTurnInstructions = InferenceSupport.Supported,
                RichContentSupported = RichContentRoutes.Responses,
                HostedTools = new HashSet<LlmHostedToolKind> { LlmHostedToolKind.WebSearch },
                Agent = ManagedAgentPolicy.Capabilities
            }, Learned).GetAwaiter().GetResult();
        public LearnedModelContext Learned { get; } = new();
        /// <summary>The test model's context window.</summary>
        public long Window { get; init; } = 1000;
        public string? EstimateDiagnostic => null;

        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request,
            IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> resources)
        {
            Estimates.Add(request);
            return UnknownEstimate ? null
                : new ContextEstimate(request.Messages.Sum(message => message.Content.Length + message.ToolResults
                    .OfType<LlmToolResult>().SelectMany(result => result.Output.Parts).OfType<LlmToolOutputText>()
                    .Sum(part => part.Text.Length)) + (request.Instructions?.Length ?? 0));
        }
        private async IAsyncEnumerable<LlmStreamEvent> Stream(LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (request.Instructions?.StartsWith("Summarize these conversation records", StringComparison.Ordinal) == true)
            {
                Summaries.Add(request);
                if (SummaryFailure is not null) throw SummaryFailure;
                yield return new LlmChatCompletionEvent(new()
                { Diagnostics = new(200, null, null), Text = SummaryText, Status = LlmCompletionStatus.Completed });
                yield break;
            }
            Requests.Add(request);
            if (ExposeBeforeFailure) { yield return new LlmTextDeltaEvent("partial"); }
            Exception? failure = RequestFailure is null ? Failure : RequestFailure(Requests.Count);
            if (failure is not null) { throw failure; }
            foreach (var snapshot in Snapshots) yield return new LlmStructuredOutputSnapshotEvent(snapshot);
            if (UsageObservations.TryDequeue(out var usage))
            {
                foreach (var observation in usage) yield return new LlmUsageDeltaEvent(observation);
            }
            yield return new LlmChatCompletionEvent(Results.Dequeue());
        }
    }
}
