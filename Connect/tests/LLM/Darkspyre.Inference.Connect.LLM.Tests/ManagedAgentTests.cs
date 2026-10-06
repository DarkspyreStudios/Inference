using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ManagedAgentTests
{
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
    public async Task HostToolsAreSentNonStrictUnlessTheHostRequestsStrictness()
    {
        // An optional property without additionalProperties is a valid host schema but not a strict one.
        JsonElement loose = JsonElement.Parse(
            """{"type":"object","properties":{"command":{"type":"string"},"timeout":{"type":"integer"}},"required":["command"]}""");
        var harness = new Harness();
        harness.Results.Enqueue(new() { Diagnostics = new(200, null, null), Text = "done", Status = LlmCompletionStatus.Completed });
        await using var session = harness.Create(ToolsOptions(_ => new()) with
        {
            Tools = [new LlmAgentToolDefinition() { Name = "bash", InputSchema = loose }, new LlmAgentToolDefinition() { Name = "echo", InputSchema = loose, Strict = true }]
        });
        await Read(session.RunTurnAsync(Turn("hello")));
        LlmToolDefinition[] sent = [.. harness.Requests.Single().Tools.Select(tool => Assert.IsType<LlmToolDefinition>(tool))];
        Assert.Equal([("bash", false), ("echo", true)], sent.Select(tool => (tool.Name, tool.Strict)));
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
        public Exception? Failure { get; set; }
        public IReadOnlyList<JsonElement> Snapshots { get; init; } = [];
        public bool UnknownEstimate { get; init; }
        public bool ExposeBeforeFailure { get; init; }
        public ManagedAgentSession Create(LlmAgentSessionOptions? options = null) => ManagedAgentSession.CreateAsync(
            new() { Version = "1", Transport = LlmTransport.OpenAiCodex, Endpoint = "https://provider.test/codex" },
            options ?? Options(), [new("test", "Test") { MaxContextTokens = 1000 }, new("other", "Other") { MaxContextTokens = 2000 }], this, Stream,
            _ => new()
            {
                SupportsAgentSessions = true,
                SupportsAgentToolUse = true,
                SupportsAgentTurnInstructions = true,
                RichContentSupported = RichContentRoutes.Responses,
                Agent = ManagedAgentPolicy.Capabilities
            }).GetAwaiter().GetResult();
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
