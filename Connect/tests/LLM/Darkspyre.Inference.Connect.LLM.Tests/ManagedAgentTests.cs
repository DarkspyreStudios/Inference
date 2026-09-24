using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ManagedAgentTests
{
    [Fact]
    public async Task ProposalAccountingUsesFrozenProjectionWithoutInstallingOrConsumingPause()
    {
        var harness = new Harness();
        await using var session = harness.Create(ToolsOptions(_ => new()) with
        {
            Instructions = "base",
            Context = History(new string('x', 90)),
            ExecutionOptions = SmallPolicy()
        });
        var pause = Assert.Single((await Read(session.RunTurnAsync(Turn("hello") with
        { ModelId = "other", Instructions = "run", ReasoningLevel = LlmReasoningLevel.High }))).OfType<LlmAgentCompactionRequiredEvent>());
        await session.SteerAsync(pause.TurnId, [new LlmAgentTextContent("queued")]);
        var captured = await session.CaptureContextAsync(new() { IncludeAttachmentData = true });
        var originalBytes = JsonSerializer.Serialize(captured);
        var proposal = captured with { Revision = 999, Entries = captured.Entries.Skip(1).ToArray() };
        var result = await session.MeasureContextAsync(proposal);
        var projected = harness.Estimates.Last();
        Assert.True(result.IsSupported);
        Assert.Equal(captured.Revision, result.ContextRevision);
        Assert.Equal(14, result.UsedContextTokens); // hello + queued + run
        Assert.Equal(90, result.InputBudgetTokens);
        Assert.Equal("other", result.ModelId);
        Assert.Equal(2000, result.ModelMaxContextTokens);
        Assert.Equal("run", projected.Instructions);
        Assert.Equal(LlmReasoningLevel.High, projected.ReasoningLevel);
        Assert.Single(projected.Tools);
        Assert.Equal(originalBytes, JsonSerializer.Serialize(await session.CaptureContextAsync(new() { IncludeAttachmentData = true })));
        Assert.Empty(harness.Requests);
        Assert.Equal(result, await session.MeasureContextAsync(proposal));
        harness.Results.Enqueue(new() { Text = "done", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
        await Read(session.ContinueTurnAsync(new(pause.TurnId, pause.PauseId, captured.Revision, proposal)));
        Assert.Equal(JsonSerializer.Serialize(projected), JsonSerializer.Serialize(Assert.Single(harness.Requests)));
    }

    [Fact]
    public async Task IdleProposalUsesBaseSettingsAndUnknownOmissionsDoNotBecomeFreeInput()
    {
        var harness = new Harness();
        await using var session = harness.Create(Options() with { Instructions = "base", Context = History("original") });
        var before = JsonSerializer.Serialize(await session.CaptureContextAsync());
        var proposal = History("replacement");
        var result = await session.MeasureContextAsync(proposal);
        Assert.Equal(15, result.UsedContextTokens);
        Assert.Equal("base", harness.Estimates.Last().Instructions);
        var omitted = proposal with
        {
            Entries = [proposal.Entries[0] with
        { Message = new("user", "replacement") { OmittedAttachments = [new(LlmAttachmentKind.Document, "missing.txt", 0)] } }]
        };
        var unknown = await session.MeasureContextAsync(omitted);
        Assert.Null(unknown.UsedContextTokens);
        Assert.Equal(LlmContextMeasurementSource.Unknown, unknown.Source);
        await Assert.ThrowsAsync<ArgumentException>(() => session.MeasureContextAsync(proposal with { Entries = [proposal.Entries[0], proposal.Entries[0]] }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.MeasureContextAsync(proposal, cancelled.Token));
        Assert.Equal(before, JsonSerializer.Serialize(await session.CaptureContextAsync()));
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task ToolsSettleInOrderAndInstructionsReasoningAndStateSpanTheLogicalRun()
    {
        var harness = new Harness();
        var executed = new List<string>();
        harness.Results.Enqueue(new()
        { Diagnostics = new(200, null, null), Text = "", Status = LlmCompletionStatus.RequiresAction, ToolCalls = [new("a", "echo", "{}"), new("b", "echo", "{}")], Usage = new(10, 2, 12) });
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "done", Status = LlmCompletionStatus.Completed, Usage = new(20, 3, 23) });
        await using var session = harness.Create(ToolsOptions(call =>
        {
            executed.Add(call.CallId);
            return new() { Content = [new LlmAgentTextContent(call.CallId)] };
        }) with
        { Instructions = "base" });
        var events = await Read(session.RunTurnAsync(Turn("hello") with { Instructions = "override", ReasoningLevel = LlmReasoningLevel.High }));
        Assert.Equal(["a", "b"], executed);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.All(harness.Requests, request => { Assert.Equal("override", request.Instructions); Assert.Equal(LlmReasoningLevel.High, request.ReasoningLevel); });
        Assert.Single(events.Select(item => item.TurnId).Distinct());
        Assert.Equal(35, events.OfType<LlmAgentUsageEvent>().Last().RunTotals?.TotalTokens);
        var context = await session.CaptureContextAsync();
        Assert.Equal(4, context.Entries.Count);
        Assert.Equal(["a", "b"], context.Entries[2].Message.ToolResults.Select(result => result.ToolCallId));
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "next", Status = LlmCompletionStatus.Completed });
        await Read(session.RunTurnAsync(Turn("new") with { Instructions = "" }));
        Assert.Equal("", harness.Requests.Last().Instructions);
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "last", Status = LlmCompletionStatus.Completed });
        await Read(session.RunTurnAsync(Turn("new")));
        Assert.Equal("base", harness.Requests.Last().Instructions);
        Assert.Null(harness.Requests.Last().ReasoningLevel);
    }

    private static readonly string[] CommandOnly = ["command"];

    [Fact]
    public async Task HostToolsAreSentNonStrictUnlessTheHostRequestsStrictness()
    {
        // An optional property without additionalProperties is a valid host schema but not a strict one.
        JsonElement loose = JsonSerializer.SerializeToElement(new
        { type = "object", properties = new { command = new { type = "string" }, timeout = new { type = "integer" } }, required = CommandOnly });
        var harness = new Harness();
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "done", Status = LlmCompletionStatus.Completed });
        await using var session = harness.Create(ToolsOptions(_ => new()) with
        {
            Tools = [new() { Name = "bash", InputSchema = loose }, new() { Name = "echo", InputSchema = loose, Strict = true }]
        });
        await Read(session.RunTurnAsync(Turn("hello")));
        LlmToolDefinition[] sent = [.. harness.Requests.Single().Tools];
        Assert.Equal([("bash", false), ("echo", true)], sent.Select(tool => (tool.Name, tool.Strict)));
        Assert.Equal(loose.GetRawText(), sent[0].InputSchema.GetRawText()); // The schema itself is sent unchanged.
    }

    [Fact]
    public async Task CompactionUsesAtomicSingleUseContinuationWithoutDuplicatingInput()
    {
        var harness = new Harness();
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "done", Status = LlmCompletionStatus.Completed });
        await using var session = harness.Create(Options() with { Context = History(new string('x', 90)), ExecutionOptions = SmallPolicy() });
        var events = await Read(session.RunTurnAsync(Turn("hello") with { Instructions = "run" }));
        var pause = Assert.Single(events.OfType<LlmAgentCompactionRequiredEvent>());
        Assert.True(pause.CanCompactHistory);
        Assert.Empty(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Empty(harness.Requests);
        var captured = await session.CaptureContextAsync(new() { IncludeAttachmentData = true });
        var replacement = captured with { Entries = captured.Entries.Skip(1).ToArray() };
        var continuation = new LlmAgentTurnContinuation(pause.TurnId, pause.PauseId, captured.Revision, replacement);
        await Assert.ThrowsAsync<ArgumentException>(() => Read(session.ContinueTurnAsync(continuation with
        { ReplacementContext = replacement with { Entries = [] } })));
        Assert.Equal(captured.Revision, (await session.CaptureContextAsync()).Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(session.ContinueTurnAsync(continuation with
        { ExpectedContextRevision = captured.Revision - 1 })));
        var continued = await Read(session.ContinueTurnAsync(continuation));
        Assert.Equal(pause.TurnId, Assert.Single(continued.OfType<LlmAgentTurnContinuedEvent>()).TurnId);
        Assert.Single(harness.Requests.Single().Messages);
        Assert.Equal("hello", harness.Requests.Single().Messages[0].Content);
        Assert.Equal("run", harness.Requests.Single().Instructions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(session.ContinueTurnAsync(continuation)));
    }

    [Fact]
    public async Task PausedSteeringIsIncludedAtomicallyWhenTheHostReplacementIsAdmitted()
    {
        var harness = new Harness();
        await using var session = harness.Create(Options() with { Context = History(new string('x', 90)), ExecutionOptions = SmallPolicy() });
        var pause = Assert.Single((await Read(session.RunTurnAsync(Turn("hello")))).OfType<LlmAgentCompactionRequiredEvent>());
        var before = await session.CaptureContextAsync();
        await session.SteerAsync(pause.TurnId, [new LlmAgentTextContent("also this")]);
        var after = await session.CaptureContextAsync();
        Assert.Equal(before.Revision, after.Revision);
        Assert.True((await session.GetContextStatusAsync()).UsedContextTokens > 95);
        var replacement = after with { Entries = after.Entries.Skip(1).Take(1).ToArray() };
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "ok", Status = LlmCompletionStatus.Completed });
        await Read(session.ContinueTurnAsync(new(pause.TurnId, pause.PauseId, after.Revision, replacement)));
        Assert.Equal("also this", harness.Requests.Single().Messages[^1].Content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SteerAsync(pause.TurnId, [new LlmAgentTextContent("stale")]));
    }

    [Fact]
    public async Task OversizeNewInputDoesNotLoopOrSendAModelRequest()
    {
        var harness = new Harness();
        await using var session = harness.Create(Options() with { ExecutionOptions = SmallPolicy() });
        var preview = await session.GetContextStatusAsync(Turn(new string('x', 100)));
        Assert.True(preview.NeedsCompaction);
        Assert.Equal(0, (await session.CaptureContextAsync()).Revision);
        var pause = Assert.Single((await Read(session.RunTurnAsync(Turn(new string('x', 100))))).OfType<LlmAgentCompactionRequiredEvent>());
        Assert.False(pause.CanCompactHistory);
        Assert.Empty(harness.Requests);
    }

    [Fact]
    public async Task ProviderOverflowIsAHostHandoffAndKeepsTheRunOverride()
    {
        var harness = new Harness
        {
            Failure = new LlmProviderException("overflow", LlmTransport.OpenAiCodex,
            providerCode: "context_length_exceeded"),
            UnknownEstimate = true
        };
        await using var session = harness.Create(Options() with { Context = History("old") });
        var pause = Assert.Single((await Read(session.RunTurnAsync(Turn("hello") with { Instructions = "override" })))
            .OfType<LlmAgentCompactionRequiredEvent>());
        Assert.Equal(LlmAgentCompactionReason.ProviderOverflow, pause.Reason);
        var context = await session.CaptureContextAsync();
        harness.Failure = null;
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "ok", Status = LlmCompletionStatus.Completed });
        await Read(session.ContinueTurnAsync(new(pause.TurnId, pause.PauseId, context.Revision,
            context with { Entries = context.Entries.Skip(1).ToArray() })));
        Assert.All(harness.Requests, request => Assert.Equal("override", request.Instructions));
    }

    [Fact]
    public async Task CaptureOmitsBytesOnlyInTheSnapshotAndRestoresStateWithExplicitIdentity()
    {
        var harness = new Harness();
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "ok", Status = LlmCompletionStatus.Completed });
        byte[] data = Encoding.UTF8.GetBytes("document");
        await using var session = harness.Create();
        await Read(session.RunTurnAsync(Turn("read") with
        {
            Attachments = [new() { Kind = LlmAttachmentKind.Document,
            FileName = "input.txt", Data = data }]
        }));
        data[0] = 0;
        var lightweight = await session.CaptureContextAsync();
        Assert.Empty(lightweight.Entries[0].Message.Attachments);
        Assert.Equal("input.txt", Assert.Single(lightweight.Entries[0].Message.OmittedAttachments).FileName);
        var full = await session.CaptureContextAsync(new() { IncludeAttachmentData = true });
        Assert.Equal("document", Encoding.UTF8.GetString(full.Entries[0].Message.Attachments[0].Data.Span));
        var serialized = JsonSerializer.Serialize(full);
        await using var reopened = harness.Create(Options() with
        {
            Context = JsonSerializer.Deserialize<LlmAgentContext>(serialized),
            ThreadId = session.ThreadId,
            SessionId = session.SessionId
        });
        Assert.Equal(session.ThreadId, reopened.ThreadId);
        await using var fork = harness.Create(Options() with { Context = full });
        Assert.NotEqual(session.ThreadId, fork.ThreadId);
        await reopened.ReplaceContextAsync(full with { Entries = [] }, full.Revision);
        Assert.Empty((await reopened.CaptureContextAsync()).Entries);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ReplaceContextAsync(full, full.Revision));
        Assert.Equal(2, (await session.CaptureContextAsync()).Entries.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelExecutionIsBoundedAndResultsRetainAssistantOrder(bool forceSequential)
    {
        var harness = new Harness();
        harness.Results.Enqueue(new()
        { Diagnostics = new(200, null, null), Text = "", Status = LlmCompletionStatus.RequiresAction, ToolCalls = Enumerable.Range(0, 4).Select(i => new LlmToolCall(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "echo", "{}")).ToArray() });
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "ok", Status = LlmCompletionStatus.Completed });
        int active = 0, peak = 0;
        var firstTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = ToolsOptions(_ => new());
        options = options with
        {
            Tools = [options.Tools[0] with { RequiresSequentialExecution = forceSequential }],
            ExecutionOptions = new() { ToolExecutionMode = LlmAgentToolExecutionMode.Parallel, MaxConcurrentTools = 2 },
            Handlers = new()
            {
                ToolHandler = async (call, token) =>
            {
                int count = Interlocked.Increment(ref active);
                int seen;
                do { seen = Volatile.Read(ref peak); } while (count > seen && Interlocked.CompareExchange(ref peak, count, seen) != seen);
                if (count == 2) { firstTwo.TrySetResult(); }
                if (!forceSequential && int.Parse(call.CallId, System.Globalization.CultureInfo.InvariantCulture) < 2) { await firstTwo.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
                Interlocked.Decrement(ref active);
                return new();
            }
            }
        };
        await using var session = harness.Create(options);
        await Read(session.RunTurnAsync(Turn("go")));
        Assert.Equal(forceSequential ? 1 : 2, peak);
        Assert.Equal(["0", "1", "2", "3"], (await session.CaptureContextAsync()).Entries[2].Message.ToolResults.Select(result => result.ToolCallId));
    }

    [Fact]
    public async Task InterruptSettlesIssuedToolsAndRejectsConcurrentOwnership()
    {
        var harness = new Harness();
        harness.Results.Enqueue(new()
        { Diagnostics = new(200, null, null), Text = "", Status = LlmCompletionStatus.RequiresAction, ToolCalls = [new("a", "echo", "{}"), new("b", "echo", "{}")] });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = ToolsOptions(_ => new()) with
        {
            Handlers = new()
            {
                ToolHandler = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new();
        }
            }
        };
        await using var session = harness.Create(options);
        string? turnId = null;
        async Task Consume()
        {
            await foreach (var item in session.RunTurnAsync(Turn("go"))) { turnId ??= item.TurnId; }
        }
        var running = Consume();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CaptureContextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.MeasureContextAsync(History("proposal")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(session.RunTurnAsync(Turn("overlap"))));
        await session.InterruptAsync(turnId!);
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        var captured = await session.CaptureContextAsync();
        Assert.Equal(2, captured.Entries[^1].Message.ToolResults.Count);
        Assert.All(captured.Entries[^1].Message.ToolResults, result => Assert.True(result.IsError));
        Assert.Contains("unknown", captured.Entries[^1].Message.ToolResults[0].Content);
        Assert.Contains("not executed", captured.Entries[^1].Message.ToolResults[1].Content);
        await using var restored = harness.Create(options with { Context = captured });
        Assert.Equal(captured.Revision, (await restored.CaptureContextAsync()).Revision);
    }

    [Theory]
    [InlineData("unknown", "{}")]
    [InlineData("echo", "{\"wrong\":true}")]
    public async Task UnknownToolsAndSchemaFailuresReturnErrorsWithoutExecuting(string name, string args)
    {
        var harness = new Harness();
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "", Status = LlmCompletionStatus.RequiresAction, ToolCalls = [new("a", name, args)] });
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "done", Status = LlmCompletionStatus.Completed });
        int executions = 0;
        await using var session = harness.Create(ToolsOptions(_ => { executions++; return new(); }));
        var events = await Read(session.RunTurnAsync(Turn("go")));
        Assert.Equal(0, executions);
        Assert.Contains(events.OfType<LlmAgentToolEvent>(), item => item.Status == "failed");
        Assert.True((await session.CaptureContextAsync()).Entries[2].Message.ToolResults[0].IsError);
    }

    [Theory]
    [InlineData(LlmCompletionStatus.Incomplete)]
    [InlineData(LlmCompletionStatus.Refused)]
    [InlineData(LlmCompletionStatus.Failed)]
    public async Task FailedOrTruncatedOutputsNeverExecuteToolCalls(LlmCompletionStatus status)
    {
        var harness = new Harness();
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "partial", Status = status, ToolCalls = [new("a", "echo", "{}")] });
        int executions = 0;
        await using var session = harness.Create(ToolsOptions(_ => { executions++; return new(); }));
        var events = await Read(session.RunTurnAsync(Turn("go")));
        Assert.Equal(0, executions);
        Assert.Equal(LlmAgentTurnStatus.Failed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Status);
        Assert.Single((await session.CaptureContextAsync()).Entries);
    }

    [Fact]
    public async Task RetryIsBoundedBeforeOutputAndUsageBecomesUnknown()
    {
        var harness = new Harness
        {
            Failure = new LlmProviderException("temporary", LlmTransport.OpenAiCodex,
            HttpStatusCode.ServiceUnavailable, retryAfter: TimeSpan.Zero)
        };
        await using var session = harness.Create();
        var events = await Read(session.RunTurnAsync(Turn("hello")));
        Assert.Equal(3, harness.Requests.Count);
        Assert.Equal(LlmAgentTurnStatus.Failed, events.OfType<LlmAgentTurnCompletedEvent>().Single().Status);
    }

    [Fact]
    public async Task PoliciesUnknownCountsAndInvalidHistoryAreHonestAndAtomic()
    {
        var harness = new Harness { UnknownEstimate = true };
        await using var session = harness.Create();
        var status = await session.GetContextStatusAsync(Turn("hello"));
        Assert.Null(status.UsedContextTokens);
        Assert.Null(status.NeedsCompaction);
        Assert.Null(status.AvailableInputTokens);
        Assert.False(status.IsEstimated);
        Assert.Throws<ArgumentException>(() => harness.Create(Options() with { ExecutionOptions = new() { MaxModelIterations = 0 } }));
        Assert.Throws<ArgumentException>(() => harness.Create(Options() with
        { ExecutionOptions = new() { ContextPolicy = new() { MaxContextTokens = 1001 } } }));
        var invalid = History("old") with { Entries = [new("x", "t", new("assistant", "") { ToolCalls = [new("a", "echo", "{}")] })] };
        await Assert.ThrowsAsync<ArgumentException>(() => session.ReplaceContextAsync(invalid, 0));
        Assert.Empty((await session.CaptureContextAsync()).Entries);
    }

    [Fact]
    public async Task IterationLimitStillSettlesTheFinalToolBatch()
    {
        var harness = new Harness();
        harness.Results.Enqueue(new()
        {
            Text = "",
            Status = LlmCompletionStatus.RequiresAction,
            Diagnostics = new(200, null, null),
            ToolCalls = [new("a", "echo", "{}")]
        });
        await using var session = harness.Create(ToolsOptions(_ => new()) with { ExecutionOptions = new() { MaxModelIterations = 1 } });
        var events = await Read(session.RunTurnAsync(Turn("go")));
        Assert.Equal(LlmAgentCompletionReason.IterationLimit, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Single(harness.Requests);
        Assert.Single((await session.CaptureContextAsync()).Entries[^1].Message.ToolResults);
    }

    [Fact]
    public async Task ExposedFailureIsNeverRetriedAndAbandonedEnumerationReleasesOwnership()
    {
        var harness = new Harness
        {
            ExposeBeforeFailure = true,
            Failure = new LlmProviderException("temporary", LlmTransport.OpenAiCodex, HttpStatusCode.ServiceUnavailable)
        };
        await using var session = harness.Create();
        var events = await Read(session.RunTurnAsync(Turn("go")));
        Assert.Single(events.OfType<LlmAgentTextDeltaEvent>());
        Assert.Single(harness.Requests);
        Assert.Equal(LlmAgentTurnStatus.Failed, events.OfType<LlmAgentTurnCompletedEvent>().Single().Status);
        harness.Failure = null;
        harness.Results.Enqueue(new() { Text = "ok", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
        await foreach (var item in session.RunTurnAsync(Turn("next")))
        {
            Assert.IsType<LlmAgentTurnStartedEvent>(item);
            break;
        }
        _ = await session.CaptureContextAsync();
    }

    [Fact]
    public async Task PendingSteeringCannotBeLostThroughAnInsufficientReplacement()
    {
        var harness = new Harness();
        await using var session = harness.Create(Options() with { Context = History(new string('x', 90)), ExecutionOptions = SmallPolicy() });
        var pause = (await Read(session.RunTurnAsync(Turn("hi")))).OfType<LlmAgentCompactionRequiredEvent>().Single();
        await session.SteerAsync(pause.TurnId, [new LlmAgentTextContent(new string('y', 100))]);
        var context = await session.CaptureContextAsync();
        var replacement = context with { Entries = context.Entries.Skip(1).ToArray() };
        await Assert.ThrowsAsync<ArgumentException>(() => Read(session.ContinueTurnAsync(new(pause.TurnId,
            pause.PauseId, context.Revision, replacement))));
        Assert.Equal(context.Revision, (await session.CaptureContextAsync()).Revision);
        Assert.True((await session.GetContextStatusAsync()).NeedsCompaction);
        await session.InterruptAsync(pause.TurnId);
        Assert.Equal(92, (await session.GetContextStatusAsync()).UsedContextTokens);
    }

    [Fact]
    public void SnapshotImportRejectsDuplicateIdsAndBrokenToolGroups()
    {
        var harness = new Harness();
        var entry = new LlmAgentContextEntry("id", "turn", new("user", "hi"));
        Assert.Throws<ArgumentException>(() => harness.Create(Options() with { Context = new() { Entries = [entry, entry] } }));
        Assert.Throws<ArgumentException>(() => harness.Create(Options() with { Context = new() { FormatVersion = 99 } }));
        Assert.Throws<ArgumentException>(() => harness.Create(Options() with
        {
            Context = new()
            { Entries = [entry with { Message = new("user", "") { ToolResults = [new("orphan", "result")] } }] }
        }));
    }

    private static LlmAgentTurnRequest Turn(string text) => new() { Input = [new LlmAgentTextContent(text)] };
    private static LlmAgentSessionOptions Options() => new() { ModelId = "test", WorkingDirectory = Environment.CurrentDirectory };
    private static LlmAgentContext History(string text) => new() { Entries = [new("old", "previous", new("user", text))] };
    private static LlmAgentExecutionOptions SmallPolicy() => new()
    { ContextPolicy = new() { MaxContextTokens = 100, ReservedOutputTokens = 10, SafetyMarginTokens = 0, CompactionThresholdRatio = .8 } };
    private static LlmAgentSessionOptions ToolsOptions(Func<LlmAgentToolCall, LlmAgentToolResult> handler) => Options() with
    {
        Tools = [new() { Name = "echo", InputSchema = JsonSerializer.SerializeToElement(new
        { type = "object", properties = new { }, additionalProperties = false }) }],
        Handlers = new() { ToolHandler = (call, _) => ValueTask.FromResult(handler(call)) }
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
        public List<LlmChatRequest> Requests { get; } = [];
        public List<LlmChatRequest> Estimates { get; } = [];
        public Exception? Failure { get; set; }
        public bool UnknownEstimate { get; init; }
        public bool ExposeBeforeFailure { get; init; }
        public ManagedAgentSession Create(LlmAgentSessionOptions? options = null) => new(
            new() { Version = "1", Transport = LlmTransport.OpenAiCodex, Endpoint = "https://provider.test/codex" },
            options ?? Options(), [new("test", "Test") { MaxContextTokens = 1000 }, new("other", "Other") { MaxContextTokens = 2000 }], this, Stream,
            _ => new()
            {
                SupportsAgentSessions = true,
                SupportsAgentToolUse = true,
                SupportsAgentTurnInstructions = true,
                SupportsToolResultImages = true,
                SupportsToolResultDocuments = true,
                Agent = ManagedAgentPolicy.Capabilities
            });
        public long? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
        {
            Estimates.Add(request);
            return UnknownEstimate ? null
                : request.Messages.Sum(message => message.Content.Length) + (request.Instructions?.Length ?? 0);
        }
        private async IAsyncEnumerable<LlmStreamEvent> Stream(LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (ExposeBeforeFailure) { yield return new LlmTextDeltaEvent("partial"); }
            if (Failure is not null) { throw Failure; }
            yield return new LlmCompletionEvent(Results.Dequeue());
        }
    }
}
