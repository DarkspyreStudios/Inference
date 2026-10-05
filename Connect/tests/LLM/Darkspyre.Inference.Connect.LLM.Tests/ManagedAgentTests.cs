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
            Tools = [new LlmAgentToolDefinition() { Name = "bash", InputSchema = loose }, new LlmAgentToolDefinition() { Name = "echo", InputSchema = loose, Strict = true }]
        });
        await Read(session.RunTurnAsync(Turn("hello")));
        LlmToolDefinition[] sent = [.. harness.Requests.Single().Tools.Select(tool => Assert.IsType<LlmToolDefinition>(tool))];
        Assert.Equal([("bash", false), ("echo", true)], sent.Select(tool => (tool.Name, tool.Strict)));
        Assert.Equal(loose.GetRawText(), sent[0].InputSchema.GetRawText()); // The schema itself is sent unchanged.
    }

    private static LlmAgentTurnRequest Turn(string text) => new() { Input = [new LlmAgentTextContent(text)] };
    private static LlmAgentSessionOptions Options() => new() { ModelId = "test" };
    private static LlmAgentSessionOptions ToolsOptions(Func<LlmAgentToolCall, LlmToolOutput> handler) => Options() with
    {
        Tools = [new LlmAgentToolDefinition() { Name = "echo", InputSchema = JsonSerializer.SerializeToElement(new
        { type = "object", properties = new { }, additionalProperties = false }) }],
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
