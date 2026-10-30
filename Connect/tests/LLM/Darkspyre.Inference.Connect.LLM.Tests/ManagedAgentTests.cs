using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
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
            Diagnostics = new(200, null, null),
            Text = string.Empty,
            Status = LlmCompletionStatus.RequiresAction,
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
            Diagnostics = new(200, null, null),
            Status = LlmCompletionStatus.Completed,
            ToolCalls = [new LlmToolCall("call-1", "echo", "{}")],
            Text = "tool step"
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

    /// <summary>A retried stream interruption reports the wait before the next attempt and the failure that caused it.</summary>
    [Fact]
    public async Task StreamInterruptionReportsDelayAndError()
    {
        var harness = new Harness { RequestFailure = number => number == 1 ? Interrupted() : null };
        harness.Results.Enqueue(Done());
        await using var session = harness.Create();

        var events = await Read(session.RunTurnAsync(Turn("hello")));

        var recovery = Assert.Single(events.OfType<LlmAgentResponseRecoveryEvent>());
        Assert.Equal(2, recovery.NextAttempt);
        Assert.Equal(TimeSpan.FromSeconds(1), recovery.Delay);
        Assert.Equal("The model response stream was interrupted.", recovery.Error);
    }

    /// <summary>An image-only request replays a lost stream unless the failed attempt started a hosted call.</summary>
    [Fact]
    public async Task InterruptWithImageToolRecoversWhenNoHostedCallStarted()
    {
        var images = new HashSet<LlmHostedToolKind> { LlmHostedToolKind.ImageGeneration };
        var recovering = new Harness { PreparesResources = true, HostedKinds = images, RequestFailure = number => number == 1 ? Interrupted() : null };
        recovering.Results.Enqueue(Done());
        await using (var session = recovering.Create(ImageOptions()))
        {
            var events = await Read(session.RunTurnAsync(Turn("hello")));
            Assert.Equal(2, recovering.Requests.Count);
            Assert.IsType<LlmImageGenerationTool>(Assert.Single(recovering.Requests[0].HostedTools));
            Assert.Equal(2, Assert.Single(events.OfType<LlmAgentResponseRecoveryEvent>()).NextAttempt);
            Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        }

        var started = new Harness { PreparesResources = true, HostedKinds = images, StartsHostedCall = true, Failure = Interrupted() };
        await using (var session = started.Create(ImageOptions()))
        {
            var events = await Read(session.RunTurnAsync(Turn("hello")));
            Assert.Single(started.Requests);
            Assert.Null(Assert.Single(events.OfType<LlmAgentResponseRecoveryEvent>()).NextAttempt);
            Assert.Contains("Provider-side work may already have run", Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Error);
        }
    }

    /// <summary>A session that asks for images opens and runs on a model without image generation.</summary>
    [Fact]
    public async Task ImageToolIsDroppedOnAModelWithoutImages()
    {
        var harness = new Harness { PreparesResources = true };
        harness.Results.Enqueue(Done());
        await using var session = harness.Create(ImageOptions());

        var events = await Read(session.RunTurnAsync(Turn("hello")));

        Assert.Empty(Assert.Single(harness.Requests).HostedTools);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
    }

    /// <summary>A stored context whose selection differs from the caller's current one opens with the current one.</summary>
    [Fact]
    public async Task OpeningAdoptsTheCurrentSelection()
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var access = new ResourcePreparationTests.Resources(new("workspace", "session", 4, [reference]),
            [(reference, "reference.txt", "reference text"u8.ToArray())]);
        var harness = new Harness { PreparesResources = true };
        await using var session = harness.Create(Options() with
        {
            Context = new()
            {
                ResourceSelectionVersion = new("workspace", "session", 3),
                SelectedResources = [new InferenceResourceReference("workspace", "stale")]
            },
            CallerServices = new() { Resources = access }
        });

        LlmAgentContext captured = await session.CaptureContextAsync();

        Assert.Equal([reference], captured.SelectedResources);
        Assert.Equal(new InferenceResourceSelectionVersion("workspace", "session", 4), captured.ResourceSelectionVersion);
    }

    /// <summary>Options for a session that asks for images and holds empty caller-owned resource access.</summary>
    private static LlmAgentSessionOptions ImageOptions() => Options() with
    {
        HostedTools = [new LlmImageGenerationTool()],
        CallerServices = new() { Resources = new ResourcePreparationTests.Resources(new("workspace", "session", 0, []), []) }
    };

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
            Diagnostics = new(200, null, null),
            Text = string.Empty,
            Status = LlmCompletionStatus.RequiresAction,
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
    public async Task ForeignHistoryContinuesOnStart()
    {
        // State another model wrote cannot be replayed here; it is left out and counted, and the turn runs.
        LlmContinuationState foreign = CodexReplay.Codec.Encode(Harness.Connection, "removed-model",
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
        Diagnostics = new(200, null, null),
        Text = string.Empty,
        Status = LlmCompletionStatus.RequiresAction,
        ToolCalls = calls
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
    public async Task ASentDefinitionHasNoAdditionalPropertiesAndArgumentsAreCheckedAgainstTheDeclaredSchema()
    {
        var harness = new Harness();
        harness.Results.Enqueue(Calls(new LlmToolCall("bad", "nested", """{"filter":{"city":3}}""")));
        harness.Results.Enqueue(Done());
        int toolsRun = 0;
        await using var session = harness.Create(Options() with
        {
            Tools = [new LlmAgentToolDefinition
            {
                Name = "nested",
                InputSchema = JsonElement.Parse("""
                    {"type":"object","additionalProperties":false,"properties":{"filter":{"type":"object",
                     "additionalProperties":false,"properties":{"city":{"type":"string"}}}}}
                    """)
            }],
            ToolHandler = (_, _) => { toolsRun++; return Task.FromResult(LlmToolOutput.FromText("unexpected")); }
        });

        await Read(session.RunTurnAsync(Turn("hello")));

        string sent = Assert.IsType<LlmToolDefinition>(Assert.Single(harness.Requests[0].Tools)).InputSchema.GetRawText();
        Assert.DoesNotContain("additionalProperties", sent, StringComparison.Ordinal);
        Assert.Contains("\"city\"", sent, StringComparison.Ordinal);
        LlmToolResult rejected = Assert.IsType<LlmToolResult>(Assert.Single(harness.Requests[1].Messages.SelectMany(message => message.ToolResults)));
        Assert.StartsWith("Tool 'nested' arguments were rejected:\n",
            Assert.IsType<LlmToolOutputText>(Assert.Single(rejected.Output.Parts)).Text, StringComparison.Ordinal);
        Assert.Equal(0, toolsRun);
    }

    [Fact]
    public async Task ReturnRequestsEndsTurnAtSettledBoundary()
    {
        var harness = new Harness();
        harness.Results.Enqueue(Calls(new LlmToolCall("good", "lookup", """{"city":"Paris"}"""),
            new LlmToolCall("bad", "lookup", "{}")) with
        { Text = "checking" });
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
    public async Task UpdatePlanRunsInsideConnectOnCodex()
    {
        var harness = new Harness();
        harness.Results.Enqueue(Calls(new LlmToolCall("plan", "update_plan", """{"plan":[{"step":"look","status":"in_progress"}]}""")));
        harness.Results.Enqueue(Done());
        int hostCalls = 0;
        await using var session = harness.Create(AgentAbilityTools.Apply(Options() with
        {
            ToolHandler = (_, _) => { Interlocked.Increment(ref hostCalls); return Task.FromResult(LlmToolOutput.FromText("host")); }
        }));

        await Read(session.RunTurnAsync(Turn("hello")));

        Assert.Equal(0, hostCalls);
        LlmToolResult result = Assert.IsType<LlmToolResult>(Assert.Single(harness.Requests[1].Messages.SelectMany(message => message.ToolResults)));
        Assert.Equal("Plan updated.", Assert.IsType<LlmToolOutputText>(Assert.Single(result.Output.Parts)).Text);
    }

    [Fact]
    public async Task CodexCompactsAtTriggerToTarget()
    {
        // A 10,000-token window with a 100-token reserve: trigger 8,415, target 3,465.
        var harness = new Harness { Window = 10000 };
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null),
            Text = string.Empty,
            Status = LlmCompletionStatus.RequiresAction,
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
        Assert.InRange((await harness.EstimateAsync(Harness.Connection, harness.Requests[1], ResourceSizes.None, CancellationToken.None))!.Value.Tokens, 0, 3465);
    }

    [Theory]
    [InlineData(LlmTransport.OpenAiCodex)]
    [InlineData(LlmTransport.OpenAiResponses)]
    public async Task CodexRefusalCompactsAndResendsOnce(LlmTransport transport)
    {
        LlmProviderException TooLong() => new("Your input exceeds the context window of this model.",
            transport, isContextOverflow: true)
        {
            ReportedContextLimit = transport == LlmTransport.OpenAiResponses ? 900 : null
        };
        LlmConnection connection = Harness.Connection with { Transport = transport };
        // The first refusal is compacted and the step sent again; the Host never sees it.
        // A 2,000-token window keeps the opening history, with the estimate margin, under the trigger.
        var recovered = new Harness
        {
            Window = 2000,
            RequestFailure = request => request == 1 ? TooLong() : null,
            SummaryText = "summary of turn one",
            SessionConnection = connection
        };
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
            // A limit the refusal reported is learned for this connection and model.
            Assert.Equal(transport == LlmTransport.OpenAiResponses ? 900 : null, recovered.Learned.Limit(recovered.ConnectionId, Harness.Connection, "test"));
        }

        // A second refusal in the same turn ends it with the provider's message and is not sent again.
        var refused = new Harness { Window = 2000, RequestFailure = _ => TooLong(), SessionConnection = connection };
        await using (var session = refused.Create(Options() with { Context = History(("t1", new string('a', 600), "first answer")) }))
        {
            List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

            LlmAgentTurnCompletedEvent completed = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
            Assert.Equal(LlmAgentCompletionReason.ProviderFailure, completed.Reason);
            Assert.Equal("Your input exceeds the context window of this model.", completed.Error);
            Assert.Equal(2, refused.Requests.Count);
        }
    }

    /// <summary>A context length set on the connection is the model's limit, above a learned one; without it the learned
    /// limit applies (S6a).</summary>
    [Theory]
    [InlineData(true, 1000L)]
    [InlineData(false, 300L)]
    public async Task ConfiguredContextLengthIsTheLimit(bool configured, long expected)
    {
        LlmConnection connection = Harness.Connection with
        {
            Transport = LlmTransport.OpenAiResponses,
            ModelCapabilityOverrides = configured
                ? new Dictionary<string, LlmCapabilityOverrides> { ["test"] = new() { MaxContextTokens = 1000 } }
                : new Dictionary<string, LlmCapabilityOverrides>()
        };
        var harness = new Harness { SessionConnection = connection };
        await harness.Learned.ObserveRefusalAsync(harness.ConnectionId, "test", estimated: 0, reportedTokens: null, reportedLimit: 300,
            knownLimit: 1000, CancellationToken.None);
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "answer", Status = LlmCompletionStatus.Completed });
        await using var session = harness.Create();

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

        Assert.Equal(expected, events.OfType<LlmAgentContextStatusEvent>().First().Status.MaxContextTokens);
    }

    /// <summary>Plugin tools run on the OpenAI API under their own names, and only the image tool is hosted (S2, B7).</summary>
    [Fact]
    public async Task PluginToolsRunOnTheOpenAiApi()
    {
        const string name = "mcp.server/look up";
        string alias = LlmToolNames.Alias(name);
        int posts = 0;
        using var handler = new DirectCodexTests.Handler(_ => DirectCodexTests.Sse(++posts == 1 ? """
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc","call_id":"call","name":"ALIAS","arguments":"{\"q\":1}"}}

            data: {"type":"response.completed","response":{"status":"completed","output":[],"id":"r"}}

            """.Replace("ALIAS", alias, StringComparison.Ordinal) : DirectCodexTests.Completed));
        var client = new OpenAiResponsesLlmClient(new HttpClient(handler));
        LlmConnection connection = DirectCodexTests.OpenAiConnection("http://local.test/v1");
        var calls = new List<LlmAgentToolCall>();
        await using ManagedAgentSession session = await ManagedAgentSession.CreateAsync("local", connection, new()
        {
            ModelId = "qwen3",
            Tools = [new LlmAgentToolDefinition { Name = name, InputSchema = JsonElement.Parse("""{"type":"object"}""") }],
            ToolHandler = (call, _) => { calls.Add(call); return Task.FromResult(LlmToolOutput.FromText("found")); },
            HostedTools = [new LlmImageGenerationTool()],
            CallerServices = new() { Resources = new ResourcePreparationTests.Resources(new("workspace", "session", 0, []), []) }
        }, [new("qwen3", "Qwen3") { MaxContextTokens = 131072 }], client,
            (request, token) => client.StreamAsync(connection, request, token),
            model => client.GetCapabilities(connection, model), new LearnedModelContext());

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("look it up")));

        LlmAgentTurnCompletedEvent done = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal((LlmAgentCompletionReason.Completed, (string?)null), (done.Reason, done.Error));
        LlmAgentToolCall call = Assert.Single(calls);
        Assert.Equal(name, call.Name);
        Assert.Equal(1, call.Arguments.GetProperty("q").GetInt32());
        JsonNode first = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Equal([("function", alias), ("image_generation", null)],
            first["tools"]!.AsArray().Select(tool => (tool!["type"]!.GetValue<string>(), tool["name"]?.GetValue<string>())));
        Assert.Contains("\"function_call_output\"", handler.Bodies[1], StringComparison.Ordinal);
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
        Assert.NotEmpty(harness.Summaries); // The parts' summary requests start together.
        Assert.Empty(harness.Requests);
        LlmAgentContext kept = await session.CaptureContextAsync();
        Assert.Equal([.. history.Entries.Select(entry => entry.Message.Content), "hello"],
            kept.Entries.Select(entry => entry.Message.Content));
    }

    [Fact]
    public async Task InterruptedSummaryIsSentAgainInsideTheSession()
    {
        var harness = new Harness { InterruptedSummaries = 2, SummaryText = "summary of t1" };
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "answer", Status = LlmCompletionStatus.Completed });
        await using var session = harness.Create(Options() with { Context = History(("t1", new string('a', 800), "first answer")) });

        List<LlmAgentEvent> events = await Read(session.RunTurnAsync(Turn("hello")));

        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Empty(events.OfType<LlmAgentResponseRecoveryEvent>());
        Assert.True(harness.Summaries.Count > 2); // The two interrupted requests were sent again.
        Assert.Single(events.OfType<LlmAgentCompactedEvent>());
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

    [Fact]
    public async Task EstimatedStatusCarriesTheMarginAfterTheLearnedCorrection()
    {
        var harness = new Harness { Window = 100_000 };
        harness.Learned.ObserveReport(harness.ConnectionId, "test", 1000, 2000, 1000);
        await using var session = harness.Create(Options() with { Context = History(("t1", new string('a', 400), "first answer")) });

        LlmAgentContextStatus status = await session.GetContextStatusAsync();

        // The harness counts 412 characters; the learned rate corrects them before the margin is added.
        long corrected = harness.Learned.Correct(harness.ConnectionId, "test", 412);
        Assert.NotEqual(412, corrected);
        Assert.Equal(corrected + ManagedAgentPolicy.EstimateMargin, status.UsedContextTokens);
        Assert.Equal(LlmContextMeasurementSource.Estimated, status.Source);
    }

    [Fact]
    public async Task FullyReportedStatusHasNoMargin()
    {
        var harness = new Harness();
        var saved = new SavedConversation(harness.ConnectionId, Harness.Connection, harness, harness.Learned, new(),
            framing: null, resources: null, _ => new LlmModel("test", "Test") { MaxContextTokens = 100_000, MaxOutputTokens = 100 },
            History(("t1", "question", "answer")));
        saved.ObserveReport("test", 500, 20);

        LlmAgentContextStatus reported = await saved.StatusAsync("test", null, observe: false, CancellationToken.None);
        LlmAgentContextStatus pending = await saved.StatusAsync("test", new("user", "next"), observe: false, CancellationToken.None);

        Assert.Equal(520, reported.UsedContextTokens);
        Assert.Equal(LlmContextMeasurementSource.Mixed, reported.Source);
        // The pending input is estimated, so that measurement carries the margin.
        Assert.Equal(520 + 4 + ManagedAgentPolicy.EstimateMargin, pending.UsedContextTokens);
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

    private sealed class Harness : ILlmContextEstimator, ILlmResourcePreparationClient, ILlmContinuationCodecSource
    {
        /// <summary>The replay codec of the client the harness stands in for: the OpenAI API's on an OpenAI API connection,
        /// otherwise Codex's.</summary>
        public ILlmContinuationCodec? ContinuationCodec => SessionConnection.Transport == LlmTransport.OpenAiResponses
            ? OpenAiApiReplay.Codec : CodexReplay.Codec;
        /// <summary>Whether the session may take caller-owned resource access; the harness itself prepares nothing.</summary>
        public bool PreparesResources { get; init; }
        public bool CanPrepareResources => PreparesResources;
        /// <summary>The hosted-tool kinds the default capabilities offer.</summary>
        public IReadOnlySet<LlmHostedToolKind> HostedKinds { get; init; } = new HashSet<LlmHostedToolKind> { LlmHostedToolKind.WebSearch };
        /// <summary>Whether each model request reports a started image call before its scripted failure.</summary>
        public bool StartsHostedCall { get; init; }
        public Queue<LlmChatResult> Results { get; } = new();
        public Queue<IReadOnlyList<LlmUsage>> UsageObservations { get; } = new();
        public List<LlmChatRequest> Requests { get; } = [];
        /// <summary>Compaction summary requests, answered with <see cref="SummaryText"/> or <see cref="SummaryFailure"/>.</summary>
        public List<LlmChatRequest> Summaries { get; } = [];
        public string SummaryText { get; init; } = "summary";

        /// <summary>How many summary requests, from the first, end with an interrupted stream.</summary>
        public int InterruptedSummaries { get; init; }
        public Exception? SummaryFailure { get; init; }
        public Exception? Failure { get; set; }
        /// <summary>An optional scripted failure for each numbered model request.</summary>
        public Func<int, Exception?>? RequestFailure { get; init; }
        public bool ExposeBeforeFailure { get; init; }
        /// <summary>The provider capabilities the session sees; null uses the default fixture.</summary>
        public LlmProviderCapabilities? Capabilities { get; init; }
        public static LlmConnection Connection { get; } =
            new() { Version = "1", Transport = LlmTransport.OpenAiCodex, Endpoint = "https://provider.test/codex" };
        /// <summary>The connection the session uses; <see cref="Connection"/> unless a test sets one.</summary>
        public LlmConnection SessionConnection { get; init; } = Connection;
        public ManagedAgentSession Create(LlmAgentSessionOptions? options = null) => ManagedAgentSession.CreateAsync(
            ConnectionId, SessionConnection,
            options ?? Options(), [new("test", "Test") { MaxContextTokens = Window, MaxOutputTokens = 100 }, new("other", "Other") { MaxContextTokens = 2000, MaxOutputTokens = 100 }], this, Stream,
            _ => Capabilities ?? new()
            {
                SupportsAgentSessions = InferenceSupport.Supported,
                SupportsAgentToolUse = InferenceSupport.Supported,
                SupportsAgentTurnInstructions = InferenceSupport.Supported,
                RichContentSupported = RichContentRoutes.Responses,
                HostedTools = HostedKinds,
                Agent = ManagedAgentPolicy.Capabilities
            }, Learned).GetAwaiter().GetResult();
        public LearnedModelContext Learned { get; } = new();
        /// <summary>The connection id the session learns under.</summary>
        public string ConnectionId { get; init; } = "test-connection";
        /// <summary>The test model's context window.</summary>
        public long Window { get; init; } = 1000;
        public string? EstimateDiagnostic => null;

        public ValueTask<ContextEstimate?> EstimateAsync(LlmConnection connection, LlmChatRequest request,
            IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> resources,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ContextEstimate?>(new ContextEstimate(request.Messages.Sum(message => message.Content.Length + message.ToolResults
                    .OfType<LlmToolResult>().SelectMany(result => result.Output.Parts).OfType<LlmToolOutputText>()
                    .Sum(part => part.Text.Length)) + (request.Instructions?.Length ?? 0)));
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
                if (Summaries.Count <= InterruptedSummaries) throw Interrupted();
                yield return new LlmChatCompletionEvent(new()
                { Diagnostics = new(200, null, null), Text = SummaryText, Status = LlmCompletionStatus.Completed });
                yield break;
            }
            Requests.Add(request);
            if (ExposeBeforeFailure) { yield return new LlmTextDeltaEvent("partial"); }
            if (StartsHostedCall) { yield return new LlmHostedToolCallStartedEvent("image-1", LlmHostedToolKind.ImageGeneration, "image_generation"); }
            Exception? failure = RequestFailure is null ? Failure : RequestFailure(Requests.Count);
            if (failure is not null) { throw failure; }
            if (UsageObservations.TryDequeue(out var usage))
            {
                foreach (var observation in usage) yield return new LlmUsageDeltaEvent(observation);
            }
            yield return new LlmChatCompletionEvent(Results.Dequeue());
        }
    }
}
