using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ManagedAgentTests
{
    [Fact]
    public async Task EveryManagedTurnAndProjectionUsesTheDefaultConversationCacheHint()
    {
        var harness = new Harness();
        await using var session = harness.Create(Options());
        await session.MeasureContextAsync(History("prior"));
        for (int turn = 0; turn < 2; turn++)
        {
            harness.Results.Enqueue(new() { Text = "done", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
            await Read(session.RunTurnAsync(Turn("hello")));
        }
        Assert.Equal(2, harness.Requests.Count);
        Assert.NotEmpty(harness.Estimates);
        Assert.All(harness.Requests.Concat(harness.Estimates), request =>
        {
            Assert.Equal(LlmPromptCacheScope.Conversation, request.PromptCache!.Scope);
            Assert.Equal(LlmPromptCacheRetention.Default, request.PromptCache.Retention);
        });
    }

    [Fact]
    public async Task SessionOwnerRetiresAnActiveManagedToolAndObservationFeed()
    {
        var harness = new Harness();
        harness.Results.Enqueue(new()
        {
            Text = "",
            Status = LlmCompletionStatus.RequiresAction,
            Diagnostics = new(200, null, null),
            ToolCalls = [new("call", "echo", "{}")]
        });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ManagedAgentSession inner = harness.Create(ToolsOptions(_ => new()) with
        {
            ToolHandler = async (_, token) =>
            {
                using CancellationTokenRegistration registration = token.Register(() => stopped.TrySetResult());
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return LlmToolOutput.FromText("unreachable");
            }
        });
        await using var session = new OwnedLlmAgentSession(inner, _ => { });
        Task turn = Read(session.RunTurnAsync(Turn("hello")));
        Task observations = Read(session.ObserveAsync());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observations);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => inner.GetCapabilitiesAsync());
        Assert.Single(harness.Requests);
    }

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
        var originalBytes = JsonSerializer.Serialize(captured, ConnectLlmJson.AgentContext);
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
        Assert.Equal(originalBytes, JsonSerializer.Serialize(await session.CaptureContextAsync(new() { IncludeAttachmentData = true }), ConnectLlmJson.AgentContext));
        Assert.Empty(harness.Requests);
        Assert.Equal(result, await session.MeasureContextAsync(proposal));
        harness.Results.Enqueue(new() { Text = "done", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
        await Read(session.ContinueTurnAsync(new(pause.TurnId, pause.PauseId, captured.Revision, proposal)));
        Assert.Equal(JsonSerializer.Serialize(projected, ConnectLlmJson.ChatRequest), JsonSerializer.Serialize(Assert.Single(harness.Requests), ConnectLlmJson.ChatRequest));
    }

    [Fact]
    public async Task IdleProposalUsesBaseSettingsAndUnknownOmissionsDoNotBecomeFreeInput()
    {
        var harness = new Harness();
        await using var session = harness.Create(Options() with { Instructions = "base", Context = History("original") });
        var before = JsonSerializer.Serialize(await session.CaptureContextAsync(), ConnectLlmJson.AgentContext);
        var proposal = History("replacement");
        var result = await session.MeasureContextAsync(proposal);
        Assert.Equal(15, result.UsedContextTokens);
        Assert.Equal("base", harness.Estimates.Last().Instructions);
        var omitted = proposal with
        {
            Entries = [proposal.Entries[0] with
        { Message = new("user", "replacement") { OmittedAttachments = [new(LlmBinaryKind.Document, "missing.txt", 0)] } }]
        };
        var unknown = await session.MeasureContextAsync(omitted);
        Assert.Null(unknown.UsedContextTokens);
        Assert.Equal(LlmContextMeasurementSource.Unknown, unknown.Source);
        await Assert.ThrowsAsync<ArgumentException>(() => session.MeasureContextAsync(proposal with { Entries = [proposal.Entries[0], proposal.Entries[0]] }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.MeasureContextAsync(proposal, cancelled.Token));
        Assert.Equal(before, JsonSerializer.Serialize(await session.CaptureContextAsync(), ConnectLlmJson.AgentContext));
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
            return LlmToolOutput.FromText(call.CallId);
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
    public async Task RunningUsageReplacesTheCurrentStepAndFinalUsageRemainsAuthoritative()
    {
        var harness = new Harness();
        harness.UsageObservations.Enqueue([new(10, 1, 11), new(10, 2, 12)]);
        harness.UsageObservations.Enqueue([new(20, 1, 21), new(20, 8, 28)]);
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null),
            Text = "",
            Status = LlmCompletionStatus.RequiresAction,
            ToolCalls = [new("a", "echo", "{}")],
            Usage = new(10, 3, 13)
        });
        harness.Results.Enqueue(new()
        { Diagnostics = new(200, null, null), Status = LlmCompletionStatus.Completed, Text = "done", Usage = new(20, 4, 24) });
        await using var session = harness.Create(ToolsOptions(_ => LlmToolOutput.FromText("ok")));
        var events = await Read(session.RunTurnAsync(Turn("hello")));
        LlmAgentUsageEvent[] usage = [.. events.OfType<LlmAgentUsageEvent>()];
        Assert.Equal(new int?[] { 11, 12, 13, 34, 41, 37 }, usage.Select(item => item.RunTotals?.TotalTokens));
        Assert.Equal(new int?[] { 11, 12, 13, 21, 28, 24 }, usage.Select(item => item.Usage?.TotalTokens));
        Assert.Single(usage.Take(3).Select(item => item.RequestId).Distinct());
        Assert.Single(usage.Skip(3).Select(item => item.RequestId).Distinct());
        Assert.NotEqual(usage[0].RequestId, usage[3].RequestId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedCostAndCountsReplaceInterimUsageAndRespectUnknownFinal(bool unknownFinal)
    {
        static LlmUsage Usage(int value) => new(1, 2, 3)
        {
            ProviderReportedCostUsd = value,
            HostedToolCallCount = value * 2,
            HostedSourcesUsed = 0,
            HostedToolCalls = new Dictionary<LlmHostedToolKind, int> { [LlmHostedToolKind.WebSearch] = value }
        };
        var harness = new Harness();
        harness.UsageObservations.Enqueue([Usage(1), Usage(2)]);
        harness.UsageObservations.Enqueue([Usage(1), Usage(2)]);
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null),
            Text = "",
            Status = LlmCompletionStatus.RequiresAction,
            ToolCalls = [new("a", "echo", "{}")],
            Usage = Usage(3)
        });
        harness.Results.Enqueue(new()
        { Diagnostics = new(200, null, null), Text = "done", Status = LlmCompletionStatus.Completed, Usage = unknownFinal ? null : Usage(5) });
        await using var session = harness.Create(ToolsOptions(_ => LlmToolOutput.FromText("ok")));
        LlmAgentUsageEvent[] observations = [.. (await Read(session.RunTurnAsync(Turn("hello")))).OfType<LlmAgentUsageEvent>()];
        Assert.Equal(new decimal?[] { 1, 2, 3, 4, 5, unknownFinal ? null : 8 }, observations.Select(item => item.RunTotals?.ProviderReportedCostUsd));
        Assert.Equal(new int?[] { 2, 4, 6, 8, 10, unknownFinal ? null : 16 }, observations.Select(item => item.RunTotals?.HostedToolCallCount));
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5, unknownFinal ? null : 8 }, observations.Select(item => item.RunTotals?.HostedToolCalls?[LlmHostedToolKind.WebSearch]));
        Assert.All(observations.Take(5), item => Assert.Equal(0, item.RunTotals?.HostedSourcesUsed));
        Assert.Equal(2, observations.Select(item => item.RequestId).Distinct().Count());
        if (unknownFinal) Assert.Null(observations[^1].RunTotals);
    }

    [Fact]
    public async Task NegativeHostedCountsAreUnknownInInterimAndFinalRequestFacts()
    {
        var invalid = new LlmUsage(1, 2, 3)
        {
            ProviderReportedCostUsd = -0.25m,
            HostedToolCallCount = -1,
            HostedSourcesUsed = -1,
            HostedToolCalls = new Dictionary<LlmHostedToolKind, int>
            { [LlmHostedToolKind.WebSearch] = -1, [LlmHostedToolKind.RemoteMcp] = 0 }
        };
        var harness = new Harness();
        harness.UsageObservations.Enqueue([invalid]);
        harness.Results.Enqueue(new()
        { Diagnostics = new(200, null, null), Text = "done", Status = LlmCompletionStatus.Completed, Usage = invalid });
        await using var session = harness.Create(Options());
        LlmAgentUsageEvent[] observations = [.. (await Read(session.RunTurnAsync(Turn("hello")))).OfType<LlmAgentUsageEvent>()];
        Assert.Equal(2, observations.Length);
        foreach (LlmAgentUsageEvent observation in observations)
        {
            foreach (LlmUsage usage in new[] { observation.Usage!, observation.RunTotals! })
            {
                Assert.Null(usage.HostedToolCallCount);
                Assert.Null(usage.HostedSourcesUsed);
                Assert.Equal(new KeyValuePair<LlmHostedToolKind, int>(LlmHostedToolKind.RemoteMcp, 0), Assert.Single(usage.HostedToolCalls!));
                Assert.Equal(-0.25m, usage.ProviderReportedCostUsd);
                Assert.Equal(3, usage.TotalTokens);
            }
        }
    }

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
    public async Task ObservingADisposedSessionFailsOnTheFirstRead()
    {
        var harness = new Harness();
        var session = harness.Create(Options());
        await session.DisposeAsync();

        IAsyncEnumerable<LlmAgentEvent> feed = session.ObserveAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Read(feed));
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
            Attachments = [new() { Kind = LlmBinaryKind.Document,
            FileName = "input.txt", Data = data }]
        }));
        data[0] = 0;
        var lightweight = await session.CaptureContextAsync();
        Assert.Empty(lightweight.Entries[0].Message.Attachments);
        Assert.Equal("input.txt", Assert.Single(lightweight.Entries[0].Message.OmittedAttachments).FileName);
        var full = await session.CaptureContextAsync(new() { IncludeAttachmentData = true });
        Assert.Equal("document", Encoding.UTF8.GetString(full.Entries[0].Message.Attachments[0].Data.Span));
        var serialized = JsonSerializer.Serialize(full, ConnectLlmJson.AgentContext);
        await using var reopened = harness.Create(Options() with
        {
            Context = JsonSerializer.Deserialize(serialized, ConnectLlmJson.AgentContext),
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
            ToolHandler = async (_, token) =>
    {
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return new();
    }
        };
        await using var session = harness.Create(options);
        // The tool can start before the consumer reads the turn's first item, so wait for both.
        var turn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Consume()
        {
            await foreach (var item in session.RunTurnAsync(Turn("go"))) { turn.TrySetResult(item.TurnId); }
        }
        var running = Consume();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string turnId = await turn.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CaptureContextAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.MeasureContextAsync(History("proposal")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(session.RunTurnAsync(Turn("overlap"))));
        await session.InterruptAsync(turnId);
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        var captured = await session.CaptureContextAsync();
        Assert.Equal(2, captured.Entries[^1].Message.ToolResults.Count);
        Assert.All(captured.Entries[^1].Message.ToolResults, result => Assert.True(result.Output.IsError));
        Assert.Contains("unknown", TextOf(captured.Entries[^1].Message.ToolResults[0]));
        Assert.Contains("not executed", TextOf(captured.Entries[^1].Message.ToolResults[1]));
        await using var restored = harness.Create(options with { Context = captured });
        Assert.Equal(captured.Revision, (await restored.CaptureContextAsync()).Revision);
    }

    [Fact]
    public async Task UnsupportedToolContentReachesTheModelRequestWithoutFailingTheTool()
    {
        var harness = new Harness();
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "", Status = LlmCompletionStatus.RequiresAction, ToolCalls = [new("a", "echo", "{}")] });
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "done", Status = LlmCompletionStatus.Completed });
        LlmToolOutput output = new()
        {
            Parts = [new LlmToolOutputText("heard"), new LlmToolOutputBinary(LlmBinaryKind.Audio, "audio/wav", new byte[] { 1, 2 }, "clip.wav")]
        };
        await using var session = harness.Create(ToolsOptions(_ => output));

        var events = await Read(session.RunTurnAsync(Turn("go")));

        LlmAgentToolEvent completed = Assert.Single(events.OfType<LlmAgentToolEvent>(), item => item.Status == "completed");
        Assert.True(completed.Success);
        Assert.Equal(2, completed.Output!.Parts.Count);
        LlmToolOutput sent = Assert.Single(harness.Requests[1].Messages.SelectMany(message => message.ToolResults)).Output;
        Assert.False(sent.IsError);
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<LlmToolOutputBinary>(sent.Parts[1]).Data.ToArray());
    }

    [Fact]
    public async Task CaptureWithoutBytesRecordsEachToolBinaryByKindAndPartIndex()
    {
        var harness = new Harness();
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "", Status = LlmCompletionStatus.RequiresAction, ToolCalls = [new("a", "echo", "{}")] });
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "done", Status = LlmCompletionStatus.Completed });
        byte[] png = [0x89, 0x50];
        await using var session = harness.Create(ToolsOptions(_ => new()
        {
            Parts =
            [
                new LlmToolOutputText("see"),
                new LlmToolOutputBinary(LlmBinaryKind.Image, "image/png", png, "chart.png"),
                new LlmToolOutputResource("file:///report.pdf") { Name = "report", MediaType = "application/pdf", Data = "%PDF-"u8.ToArray() },
                new LlmToolOutputResource("file:///notes.md") { Text = "kept" }
            ]
        }));
        await Read(session.RunTurnAsync(Turn("go")));
        png[0] = 0;

        LlmChatMessage light = (await session.CaptureContextAsync()).Entries[2].Message;
        LlmChatMessage full = (await session.CaptureContextAsync(new() { IncludeAttachmentData = true })).Entries[2].Message;

        Assert.Equal(
            [new LlmOmittedAttachment(LlmBinaryKind.Image, "chart.png", 1, "a"), new LlmOmittedAttachment(LlmBinaryKind.Document, "report", 2, "a")],
            light.OmittedAttachments);
        Assert.Equal(["see", "kept"], light.ToolResults[0].Output.Parts.Select(part => part switch
        {
            LlmToolOutputText text => text.Text,
            LlmToolOutputResource resource => resource.Text,
            _ => null
        }));
        Assert.Empty(full.OmittedAttachments);
        Assert.Equal(0x89, Assert.IsType<LlmToolOutputBinary>(full.ToolResults[0].Output.Parts[1]).Data.Span[0]);
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
        Assert.Contains(events.OfType<LlmAgentToolEvent>(), item => item.Status == "failed" && item.Output is { IsError: true });
        Assert.Equal("{}", Assert.Single(events.OfType<LlmAgentToolEvent>(), item => item.Status == "failed").Arguments.GetRawText());
        Assert.True((await session.CaptureContextAsync()).Entries[2].Message.ToolResults[0].Output.IsError);
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
    public async Task ResumableIncompleteReplySurvivesCaptureImportWithoutAutomaticRetryOrToolExecution()
    {
        var harness = new Harness();
        var connection = new LlmConnection
        { Version = "1", Transport = LlmTransport.OpenAiCodex, Endpoint = "https://provider.test/codex" };
        var state = ProviderContinuationStateCodec.Encode(connection, "test", JsonNode.Parse("""
            [{"type":"message","role":"assistant","content":[{"type":"output_text","text":"paused"}]}]
            """)!.AsArray());
        harness.Results.Enqueue(new()
        {
            Diagnostics = new(200, null, null),
            Text = "paused",
            Status = LlmCompletionStatus.Incomplete,
            FinishReason = "pause_turn",
            IncompleteReason = "pause_turn",
            ContinuationState = state,
            Usage = new(7, 4, 11)
        });
        int executions = 0;
        await using var session = harness.Create(ToolsOptions(_ => { executions++; return new(); }));
        var events = await Read(session.RunTurnAsync(Turn("go")));
        Assert.Single(harness.Requests);
        Assert.Equal(0, executions);
        Assert.Empty(events.OfType<LlmAgentToolEvent>());
        Assert.Equal("paused", Assert.Single(events.OfType<LlmAgentMessageCompletedEvent>()).Text);
        var completion = Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>());
        Assert.Equal(LlmAgentCompletionReason.Truncation, completion.Reason);
        Assert.Equal(11, Assert.Single(events.OfType<LlmAgentUsageEvent>()).RunTotals!.TotalTokens);
        LlmAgentContext captured = await session.CaptureContextAsync();
        Assert.Equal(2, captured.Entries.Count);
        Assert.Equal(state, captured.Entries[^1].Message.ContinuationState);

        await using var restored = harness.Create(Options() with { Context = captured });
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "done", Status = LlmCompletionStatus.Completed });
        await Read(restored.RunTurnAsync(Turn("continue")));
        Assert.Equal(state, harness.Requests[1].Messages[1].ContinuationState);
        Assert.Equal("paused", harness.Requests[1].Messages[1].Content);
        Assert.Equal(2, harness.Requests.Count);
        Assert.Equal(state, (await restored.CaptureContextAsync()).Entries[1].Message.ContinuationState);
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
        Assert.Equal(LlmContextMeasurementSource.Unknown, status.Source);
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
            { Entries = [entry with { Message = new("user", "") { ToolResults = [new("orphan", LlmToolOutput.FromText("result"))] } }] }
        }));
    }

    private static LlmAgentTurnRequest Turn(string text) => new() { Input = [new LlmAgentTextContent(text)] };
    [Fact]
    public async Task AStartContextFromAnotherModelImportsWithoutItsStateAndWarnsOnce()
    {
        var harness = new Harness();
        var foreign = new LlmContinuationState { ImplementationId = ProviderContinuationStateCodec.ImplementationId(LlmTransport.OpenAiCodex), ModelId = "other", ScopeId = "scope", Payload = "[]" };
        LlmAgentContext history = new()
        {
            Entries = [new("q", "t1", new("user", "What is 17 times 23?")), new("a", "t1", new("assistant", "391") { ContinuationState = foreign })]
        };
        Assert.Throws<ArgumentException>(() => harness.Create(Options() with { Context = history }));
        await using var session = harness.Create(Options() with { Context = history, UnboundContinuationState = LlmUnboundContinuationState.Omit });
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "396", Status = LlmCompletionStatus.Completed });
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "401", Status = LlmCompletionStatus.Completed });

        var first = await Read(session.RunTurnAsync(Turn("Add 5.")));
        var second = await Read(session.RunTurnAsync(Turn("Again.")));

        Assert.Contains("from 1 earlier messages", Assert.Single(first.OfType<LlmAgentWarningEvent>()).Message, StringComparison.Ordinal);
        Assert.Empty(second.OfType<LlmAgentWarningEvent>());
        Assert.Null(harness.Requests[0].Messages[1].ContinuationState);
        Assert.Equal("391", harness.Requests[0].Messages[1].Content);
        Assert.All((await session.CaptureContextAsync()).Entries, entry => Assert.Null(entry.Message.ContinuationState));
    }

    private static LlmAgentSessionOptions Options() => new() { ModelId = "test" };
    private static LlmAgentContext History(string text) => new() { Entries = [new("old", "previous", new("user", text))] };
    private static LlmAgentExecutionOptions SmallPolicy() => new()
    { ContextPolicy = new() { MaxContextTokens = 100, ReservedOutputTokens = 10, SafetyMarginTokens = 0, CompactionThresholdRatio = .8 } };
    private static LlmAgentSessionOptions ToolsOptions(Func<LlmAgentToolCall, LlmToolOutput> handler) => Options() with
    {
        Tools = [new() { Name = "echo", InputSchema = JsonSerializer.SerializeToElement(new
        { type = "object", properties = new { }, additionalProperties = false }) }],
        ToolHandler = (call, _) => Task.FromResult(handler(call))
    };
    private static string TextOf(LlmToolResult result) =>
        Assert.IsType<LlmToolOutputText>(Assert.Single(result.Output.Parts)).Text;
    private static async Task<List<LlmAgentEvent>> Read(IAsyncEnumerable<LlmAgentEvent> source)
    {
        var events = new List<LlmAgentEvent>();
        await foreach (var item in source) { events.Add(item); }
        return events;
    }

    [Fact]
    public void ManagedSessionsRejectNativeRuntimeOptions() =>
        Assert.Throws<NotSupportedException>(() => new Harness().Create(Options() with { Runtime = new() { WorkingDirectory = "." } }));

    [Fact]
    public async Task StructuredSnapshotsKeepIdentityAndUnknownResultTerminatesOnce()
    {
        var harness = new Harness { Snapshots = [JsonElement.Parse("{}"), JsonElement.Parse("{\"key\":\"value\"}")] };
        harness.Results.Enqueue(new() { Text = "{\"key\":\"value\"}", Status = LlmCompletionStatus.Unknown, Diagnostics = new(200, null, null) });
        await using var session = harness.Create();
        var events = await Read(session.RunTurnAsync(Turn("hello")));
        var snapshots = events.OfType<LlmAgentStructuredOutputSnapshotEvent>().ToArray();
        var message = Assert.Single(events.OfType<LlmAgentMessageCompletedEvent>());
        Assert.Equal(2, snapshots.Length);
        Assert.All(snapshots, snapshot => { Assert.Equal(message.ItemId, snapshot.ItemId); Assert.Equal(message.TurnId, snapshot.TurnId); Assert.Equal(session.ThreadId, snapshot.ThreadId); });
        Assert.Equal("value", snapshots[1].Content.GetProperty("key").GetString());
        Assert.Equal(LlmAgentCompletionReason.Unknown, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        Assert.Single(harness.Requests);
        Assert.Equal(message.Text, (await session.CaptureContextAsync()).Entries[^1].Message.Content);
    }

    private sealed class Harness : ILlmContextEstimator
    {
        public Queue<LlmChatResult> Results { get; } = new();
        public Queue<IReadOnlyList<LlmUsage>> UsageObservations { get; } = new();
        public List<LlmChatRequest> Requests { get; } = [];
        public List<LlmChatRequest> Estimates { get; } = [];
        public Exception? Failure { get; set; }
        public IReadOnlyList<JsonElement> Snapshots { get; init; } = [];
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
                RichContentSupported = RichContentRoutes.Responses,
                Agent = ManagedAgentPolicy.Capabilities
            });
        public string? EstimateDiagnostic => null;

        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
        {
            Estimates.Add(request);
            return UnknownEstimate ? null
                : new ContextEstimate(request.Messages.Sum(message => message.Content.Length) + (request.Instructions?.Length ?? 0));
        }
        private async IAsyncEnumerable<LlmStreamEvent> Stream(LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (ExposeBeforeFailure) { yield return new LlmTextDeltaEvent("partial"); }
            if (Failure is not null) { throw Failure; }
            foreach (var snapshot in Snapshots) yield return new LlmStructuredOutputSnapshotEvent(snapshot);
            if (UsageObservations.TryDequeue(out var usage))
            {
                foreach (var observation in usage) yield return new LlmUsageDeltaEvent(observation);
            }
            yield return new LlmChatCompletionEvent(Results.Dequeue());
        }
    }
}
