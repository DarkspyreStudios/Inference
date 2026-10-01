using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.BedrockAgentCore;
using Amazon.BedrockAgentCore.Model;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockAgentCoreUsageTests
{
    [Fact]
    public async Task ChatReplacesMetadataSuppressesDuplicatesAndClearsMissingFinalUsage()
    {
        var invoker = new Invoker([Metadata(4, 1, 5), Metadata(4, 1, 5), Metadata(4, 2, 6), Stop(), new HarnessMetadataEvent()]);
        LlmStreamEvent[] events = await ChatAsync(invoker);
        Assert.Equal([new LlmUsage(4, 1, 5), new LlmUsage(4, 2, 6)], events.OfType<LlmUsageDeltaEvent>().Select(item => item.Cumulative));
        Assert.Null(Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result.Usage);
    }

    [Fact]
    public async Task ChatReemitsKnownUsageAfterUnknownMetadataWithoutInventingAnUnknownEvent()
    {
        LlmStreamEvent[] events = await ChatAsync(new Invoker([Metadata(4, 1, 5), new HarnessMetadataEvent(), Metadata(4, 1, 5), Stop()]));
        Assert.Equal([new LlmUsage(4, 1, 5), new LlmUsage(4, 1, 5)], events.OfType<LlmUsageDeltaEvent>().Select(item => item.Cumulative));
        Assert.Equal(new LlmUsage(4, 1, 5), Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result.Usage);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(0, 0, 0)]
    [InlineData(4, null, 19)]
    [InlineData(null, 2, null)]
    public async Task NativeSettledUsagePreservesEverySuppliedBasicField(int? input, int? output, int? total)
    {
        var invoker = new Invoker([Stop(), Metadata(input, output, total)]);
        LlmConnection connection = Connection() with { CapabilityOverrides = new() { SupportsUsageDeltas = false } };
        await using ILlmAgentSession session = await Client(invoker).StartSessionAsync("c", connection, Options());
        LlmAgentEvent[] events = await ReadAsync(session.RunTurnAsync(Turn));
        LlmAgentUsageEvent usage = Assert.Single(events.OfType<LlmAgentUsageEvent>());
        Assert.Equal(new LlmUsage(input, output, total), usage.Usage);
        Assert.Equal(usage.Usage, usage.RunTotals);
        Assert.Null(usage.Usage!.CachedInputTokens);
        Assert.Null(usage.Usage.CacheWriteInputTokens);
        Assert.Null(usage.Usage.ReasoningOutputTokens);
        Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]);
        Assert.True(Array.IndexOf(events, usage) < events.Length - 1);
    }

    [Fact]
    public async Task NativeUnknownObservationDoesNotSuppressRestoredIdenticalUsage()
    {
        var invoker = new Invoker([Metadata(4, 1, 5), new HarnessMetadataEvent(), Metadata(4, 1, 5), Metadata(4, 1, 5), Stop()]);
        await using ILlmAgentSession session = await Client(invoker).StartSessionAsync("c", Connection(), Options());
        LlmAgentUsageEvent[] usage = (await ReadAsync(session.RunTurnAsync(Turn))).OfType<LlmAgentUsageEvent>().ToArray();
        Assert.Equal([new LlmUsage(4, 1, 5), null, new LlmUsage(4, 1, 5), new LlmUsage(4, 1, 5)], usage.Select(item => item.Usage));
        Assert.Single(usage.Select(item => item.RequestId).Distinct(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task TwoInvocationsSumOnlyDistinctSettledRounds(bool missing, bool overflow, bool missingFirst)
    {
        int input = overflow ? int.MaxValue : 4;
        var invoker = new Invoker(
            missingFirst ? Tool() : [.. Tool(), Metadata(input, 1, input), Metadata(input, 1, input)],
            missing ? [Stop()] : [Stop(), Metadata(input, 1, input)]);
        LlmConnection connection = Connection() with { CapabilityOverrides = new() { SupportsUsageDeltas = false } };
        await using ILlmAgentSession session = await Client(invoker).StartSessionAsync("c", connection, Options(tools: true));
        LlmAgentEvent[] events = await ReadAsync(session.RunTurnAsync(Turn));
        LlmAgentUsageEvent[] usage = events.OfType<LlmAgentUsageEvent>().ToArray();
        Assert.Equal(2, usage.Length);
        Assert.Equal(2, usage.Select(item => item.RequestId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(missingFirst ? null : new LlmUsage(input, 1, input), usage[0].Usage);
        Assert.Equal(missing ? null : new LlmUsage(input, 1, input), usage[1].Usage);
        Assert.Equal(missing || missingFirst ? null : new LlmUsage(overflow ? null : 8, 2, overflow ? null : 8), usage[1].RunTotals);
        Assert.Equal(2, invoker.Requests.Count);
        Assert.All(invoker.Requests, request => Assert.Equal(session.SessionId, request.RuntimeSessionId));
        Assert.Equal("call-1", invoker.Requests[1].Messages[0].Content[0].ToolUse.ToolUseId);
        Assert.Equal("call-1", invoker.Requests[1].Messages[1].Content[0].ToolResult.ToolUseId);
    }

    [Fact]
    public async Task TwoHostToolFollowUpsSettleThreeDistinctInvocations()
    {
        var invoker = new Invoker([.. Tool(), Metadata(4, 1, 5)],
            [.. Tool(), Metadata(4, 1, 5), Metadata(4, 1, 5)], [Stop(), Metadata(4, 1, 5)]);
        LlmConnection connection = Connection() with { CapabilityOverrides = new() { SupportsUsageDeltas = false } };
        await using ILlmAgentSession session = await Client(invoker).StartSessionAsync("c", connection, Options(tools: true));
        LlmAgentEvent[] events = await ReadAsync(session.RunTurnAsync(Turn));
        LlmAgentUsageEvent[] usage = events.OfType<LlmAgentUsageEvent>().ToArray();
        Assert.Equal(3, usage.Length);
        Assert.Equal(3, usage.Select(item => item.RequestId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(usage, item => Assert.Equal(new LlmUsage(4, 1, 5), item.Usage));
        Assert.Equal([new LlmUsage(4, 1, 5), new LlmUsage(8, 2, 10), new LlmUsage(12, 3, 15)], usage.Select(item => item.RunTotals));
        Assert.Equal(3, invoker.Requests.Count);
        Assert.All(invoker.Requests, request => Assert.Equal(session.SessionId, request.RuntimeSessionId));
        Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]);
    }

    [Fact]
    public async Task MissingRoundUsageRemainsUnknownAndTheNextTurnStartsANewLedger()
    {
        var invoker = new Invoker([Stop()], [Stop(), Metadata(0, 0, 0)]);
        var client = Client(invoker);
        const string sessionId = "darkspyre-inference-connect-resumed-session-123";
        LlmConnection connection = Connection() with { CapabilityOverrides = new() { SupportsUsageDeltas = false } };
        await using ILlmAgentSession session = await client.ResumeSessionAsync("c", connection, sessionId,
            new() { ModelId = "default", Runtime = Options().Runtime });
        LlmAgentUsageEvent first = Assert.Single((await ReadAsync(session.RunTurnAsync(Turn))).OfType<LlmAgentUsageEvent>());
        LlmAgentUsageEvent second = Assert.Single((await ReadAsync(session.RunTurnAsync(Turn))).OfType<LlmAgentUsageEvent>());
        Assert.Null(first.Usage);
        Assert.Null(first.RunTotals);
        Assert.Equal(new LlmUsage(0, 0, 0), second.RunTotals);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.All(invoker.Requests, request => { Assert.Equal(sessionId, request.RuntimeSessionId); Assert.Null(request.Model); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelOverrideControlsOnlyInterimObservationsInBothLanes(bool enabled)
    {
        var connection = Connection() with
        {
            CapabilityOverrides = new() { SupportsUsageDeltas = !enabled },
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides> { ["chosen"] = new() { SupportsUsageDeltas = enabled } }
        };
        var chat = new Invoker([Metadata(4, 1, 5), Stop()]);
        LlmStreamEvent[] chatEvents = await ChatAsync(chat, connection, "chosen");
        Assert.Equal(enabled ? 1 : 0, chatEvents.OfType<LlmUsageDeltaEvent>().Count());
        Assert.Equal(new LlmUsage(4, 1, 5), Assert.IsType<LlmChatCompletionEvent>(chatEvents[^1]).Result.Usage);
        var native = new Invoker([Metadata(4, 1, 5), Stop()]);
        await using ILlmAgentSession session = await Client(native).StartSessionAsync("c", connection, Options());
        LlmAgentEvent[] nativeEvents = await ReadAsync(session.RunTurnAsync(Turn with { ModelId = "chosen" }));
        Assert.Equal(enabled ? 2 : 1, nativeEvents.OfType<LlmAgentUsageEvent>().Count());
        Assert.Equal("chosen", Assert.Single(native.Requests).Model.BedrockModelConfig.ModelId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterAnObservationRejectsLateMetadataAndCompletion(bool native)
    {
        using var cancellation = new CancellationTokenSource();
        var invoker = new Invoker([Metadata(4, 1, 5), Metadata(4, 2, 6), Stop()], ignoreCancellation: true);
        if (native)
        {
            await using ILlmAgentSession session = await Client(invoker).StartSessionAsync("c", Connection(), Options());
            await using IAsyncEnumerator<LlmAgentEvent> reader = session.RunTurnAsync(Turn, cancellation.Token).GetAsyncEnumerator();
            while (await reader.MoveNextAsync() && reader.Current is not LlmAgentUsageEvent) { }
            Assert.IsType<LlmAgentUsageEvent>(reader.Current);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.MoveNextAsync().AsTask());
        }
        else
        {
            await using IAsyncEnumerator<LlmStreamEvent> reader = Client(invoker).StreamAsync(Connection(), ChatRequest(), cancellation.Token).GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            Assert.IsType<LlmUsageDeltaEvent>(reader.Current);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.MoveNextAsync().AsTask());
        }
    }

    [Fact]
    public void SettlingTheSameRoundTwiceDoesNotAddItsUsageTwice()
    {
        var usage = new BedrockHarnessUsage(true);
        usage.Observe(Metadata(4, 1, 5));
        Assert.Equal(new LlmUsage(4, 1, 5), usage.Settle());
        Assert.Equal(new LlmUsage(4, 1, 5), usage.Settle());
        usage.BeginRound();
        usage.Observe(Metadata(4, 1, 5));
        Assert.Equal(new LlmUsage(8, 2, 10), usage.Settle());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedTerminalStillSettlesActualUsageBeforeTurnCompletion(bool missingStop)
    {
        IEventStreamEvent[] response = missingStop ? [Metadata(4, 1, 19)]
            : [new HarnessMessageStopEvent { StopReason = HarnessStopReason.Max_tokens }, Metadata(4, 1, 19)];
        await using ILlmAgentSession session = await Client(new(response)).StartSessionAsync("c",
            Connection() with { CapabilityOverrides = new() { SupportsUsageDeltas = false } }, Options());
        LlmAgentEvent[] events = await ReadAsync(session.RunTurnAsync(Turn));
        LlmAgentUsageEvent usage = Assert.Single(events.OfType<LlmAgentUsageEvent>());
        Assert.Equal(new LlmUsage(4, 1, 19), usage.Usage);
        Assert.Equal(usage.Usage, usage.RunTotals);
        Assert.Equal(LlmAgentCompletionReason.ProviderFailure, Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]).Reason);
        Assert.True(Array.IndexOf(events, usage) < events.Length - 1);
    }

    private static LlmAgentTurnRequest Turn => new() { Input = [new LlmAgentTextContent("hello")] };
    private static BedrockAgentCoreLlmClient Client(Invoker invoker) => new(invoker);
    private static LlmChatRequest ChatRequest(string model = "default") => new() { ModelId = model, Messages = [new("user", "hello")] };
    private static Task<LlmStreamEvent[]> ChatAsync(Invoker invoker, LlmConnection? connection = null, string model = "default") =>
        ReadAsync(Client(invoker).StreamAsync(connection ?? Connection(), ChatRequest(model)));
    private static HarnessMetadataEvent Metadata(int? input, int? output, int? total) => new()
    {
        Usage = new() { InputTokens = input, OutputTokens = output, TotalTokens = total, CacheReadInputTokens = 7, CacheWriteInputTokens = 9 }
    };
    private static HarnessMessageStopEvent Stop() => new() { StopReason = HarnessStopReason.End_turn };
    private static IEventStreamEvent[] Tool() =>
    [
        new HarnessContentBlockStartEvent { ContentBlockIndex = 0, Start = new() { ToolUse = new() { ToolUseId = "call-1", Name = "lookup" } } },
        new HarnessContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { ToolUse = new() { Input = "{}" } } },
        new HarnessMessageStopEvent { StopReason = HarnessStopReason.Tool_use }
    ];
    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AwsBedrockAgentCore,
        Endpoint = "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/test-harness",
        Credential = new LlmCredential { Aws = new() { Region = "us-east-1", Profile = "test-profile" } }.Serialize()
    };
    private static LlmAgentSessionOptions Options(bool tools = false) => new()
    {
        ModelId = "default",
        Runtime = new() { WorkingDirectory = Environment.CurrentDirectory, Policy = new LlmAgentHostRuntimePolicy() },
        Tools = tools ? [new() { Name = "lookup", Description = "Controlled lookup", InputSchema = JsonElement.Parse("{}") }] : [],
        ToolHandler = tools ? (_, _) => Task.FromResult(LlmToolOutput.FromText("result")) : null
    };
    private static async Task<T[]> ReadAsync<T>(IAsyncEnumerable<T> stream)
    {
        var events = new List<T>();
        await foreach (T item in stream) events.Add(item);
        return [.. events];
    }
    private sealed class Invoker : IBedrockAgentCoreInvoker
    {
        private readonly Queue<IEventStreamEvent[]> _responses;
        private readonly bool _ignoreCancellation;
        internal Invoker(IEventStreamEvent[] response, bool ignoreCancellation = false) : this(new IEventStreamEvent[][] { response }) => _ignoreCancellation = ignoreCancellation;
        internal Invoker(params IEventStreamEvent[][] responses) => _responses = new(responses);
        internal List<InvokeHarnessRequest> Requests { get; } = [];
        public async IAsyncEnumerable<IEventStreamEvent> InvokeAsync(LlmAwsCredentials aws, InvokeHarnessRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            foreach (IEventStreamEvent item in _responses.Dequeue())
            {
                if (!_ignoreCancellation) cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }
    }
}
