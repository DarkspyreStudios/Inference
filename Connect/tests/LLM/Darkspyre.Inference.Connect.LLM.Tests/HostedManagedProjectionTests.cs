using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedManagedProjectionTests
{
    [Fact]
    public async Task ManagedTurnsAndMeasurementsPreserveFrozenHostedOptionsAndActualOrderedFacts()
    {
        List<string> domains = ["original.test"];
        List<LlmHostedTool> tools = [new LlmWebSearchTool() { AllowedDomains = domains }];
        var harness = new Harness();
        await using var session = harness.Create(new() { ModelId = "model", HostedTools = tools, MaxHostedTurns = 2 });
        domains.Add("late.test"); tools.Clear();
        Assert.Contains(LlmHostedToolKind.WebSearch, (await session.GetCapabilitiesAsync()).HostedTools);
        var events = await Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")] }));
        Assert.All(harness.Requests.Concat(harness.Estimates), request =>
        {
            Assert.Equal(2, request.MaxHostedTurns);
            Assert.Equal("original.test", Assert.Single(Assert.IsType<LlmWebSearchTool>(Assert.Single(request.HostedTools)).AllowedDomains!));
        });
        var started = Assert.Single(events.OfType<LlmAgentHostedToolCallStartedEvent>());
        var progress = Assert.Single(events.OfType<LlmAgentHostedToolCallProgressEvent>());
        var completed = Assert.Single(events.OfType<LlmAgentHostedToolCallCompletedEvent>());
        var usage = Assert.Single(events.OfType<LlmAgentUsageEvent>());
        Assert.Equal(started.RequestId, progress.RequestId);
        Assert.Equal(started.RequestId, completed.RequestId);
        Assert.Equal(started.RequestId, usage.RequestId);
        Assert.Equal("call", started.CallId); Assert.Equal("call", progress.CallId);
        Assert.Equal("future-status", completed.Call.Status);
        var output = Assert.IsType<LlmHostedSearchOutput>(completed.Call.Output);
        Assert.Equal(["https://one.test", "https://two.test"], output.Sources!.Select(source => source.Url));
        var citations = events.OfType<LlmAgentCitationEvent>().ToArray();
        Assert.Equal(2, citations.Length);
        Assert.Equal(harness.FirstCitation, citations[0].Citation);
        Assert.Equal(harness.SecondCitation, citations[1].Citation);
        Assert.All(citations, item => Assert.Equal(started.RequestId, item.RequestId));
        Assert.IsType<LlmAgentTurnCompletedEvent>(events[^1]);
        Assert.Empty(events.OfType<LlmAgentToolEvent>());
    }

    [Fact]
    public async Task ChangedModelRefusesHostedFeaturesBeforeEstimatorOrProducer()
    {
        var harness = new Harness();
        await using var session = harness.Create(new() { ModelId = "model", HostedTools = [new LlmWebSearchTool()] });
        int estimates = harness.Estimates.Count;
        await Assert.ThrowsAsync<NotSupportedException>(() => Read(session.RunTurnAsync(new()
        { Input = [new LlmAgentTextContent("hello")], ModelId = "unsupported" })));
        Assert.Equal(estimates, harness.Estimates.Count);
        Assert.Empty(harness.Requests);
    }

    private static async Task<IReadOnlyList<LlmAgentEvent>> Read(IAsyncEnumerable<LlmAgentEvent> stream)
    {
        List<LlmAgentEvent> events = [];
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    private sealed class Harness : ILlmContextEstimator
    {
        internal List<LlmChatRequest> Requests { get; } = [];
        internal List<LlmChatRequest> Estimates { get; } = [];
        internal LlmCitation FirstCitation { get; } = new("https://same.test", StartIndex: 0, EndIndex: 2);
        internal LlmCitation SecondCitation { get; } = new("https://same.test", StartIndex: null, EndIndex: 3);
        internal ManagedAgentSession Create(LlmAgentSessionOptions options) => new(
            new() { Transport = LlmTransport.OpenAiResponses, Version = "1", Endpoint = "https://example.test" },
            options, [], this, Stream, model => new()
            {
                SupportsAgentSessions = true, Agent = ManagedAgentPolicy.Capabilities,
                HostedTools = model == "unsupported" ? new HashSet<LlmHostedToolKind>() : new HashSet<LlmHostedToolKind> { LlmHostedToolKind.WebSearch }
            });
        public string? EstimateDiagnostic => null;
        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
        { Estimates.Add(request); return new(1); }
        private async IAsyncEnumerable<LlmStreamEvent> Stream(LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            yield return new LlmHostedToolCallStartedEvent("call", LlmHostedToolKind.WebSearch, "actual search");
            yield return new LlmHostedToolCallProgressEvent("call", LlmHostedToolPhase.Searching);
            using var input = JsonDocument.Parse("{\"query\":\"actual\"}");
            yield return new LlmHostedToolCallCompletedEvent(new("call", LlmHostedToolKind.WebSearch, "actual search", "future-status", input.RootElement,
                new LlmHostedSearchOutput([new("https://one.test"), new("https://two.test")])));
            yield return new LlmCitationEvent(FirstCitation);
            yield return new LlmCitationEvent(SecondCitation);
            yield return new LlmChatCompletionEvent(new()
            { Text = "answer", Status = LlmCompletionStatus.Completed, Citations = [FirstCitation, SecondCitation], Diagnostics = new(200, null, null) });
        }
    }
}
