using Darkspyre.Inference.Connect.LLM.Internal;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AntigravityCliLlmClientTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PinnedModelRejectsConflictingEffortBeforeInvocationAndAcceptsItsOwnLevel(bool streaming)
    {
        const string model = "gemini-3.8-flash-low";
        string output = streaming
            ? "{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\",\"response\":\"OK\"}}"
            : "{\"status\":\"SUCCESS\",\"response\":\"OK\"}";
        var runner = new RecordingRunner(new(0, output, string.Empty));
        var client = new AntigravityCliLlmClient(runner);
        LlmConnection connection = Connection() with
        {
            CapabilityOverrides = new()
            {
                SupportsStreaming = streaming,
                ReasoningLevels = LlmReasoningEffort.Levels(LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High)
            }
        };
        var request = new LlmChatRequest
        {
            ModelId = model,
            Messages = [new("user", "Reply with OK.")],
            ReasoningLevel = LlmReasoningLevel.Medium
        };

        InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() => ReadAllAsync(client.StreamAsync(connection, request)));
        Assert.Equal(InferenceFailureCode.InvalidRequest, failure.Code);
        Assert.Empty(runner.Invocations);
        Assert.Equal(LlmReasoningLevel.Low, Assert.Single(client.GetCapabilities(connection, model).ReasoningLevels));

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(connection,
            request with { ReasoningLevel = LlmReasoningLevel.Low }));
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("OK", result.Text);
        AntigravityCliInvocation invocation = Assert.Single(runner.Invocations);
        Assert.Equal(model, invocation.Arguments[invocation.Arguments.ToList().IndexOf("--model") + 1]);
        Assert.Equal("low", invocation.Arguments[invocation.Arguments.ToList().IndexOf("--effort") + 1]);
    }

    [Fact]
    public async Task ToolHistoryReachesThePromptAsLabelledBlocks()
    {
        var runner = new RecordingRunner(new AntigravityCliProcessResult(
            0,
            """
            {"event":"init","conversation_id":"conversation-1","init":{}}
            {"event":"result","result":{"conversation_id":"conversation-1","status":"SUCCESS","response":"522,000","usage":{"input_tokens":12,"output_tokens":2,"total_tokens":14}}}
            """,
            string.Empty));
        var client = new AntigravityCliLlmClient(runner);

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(
            Connection(), new LlmChatRequest { ModelId = "auto", Messages = ToolHistory() }));

        Assert.Equal("522,000", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        string input = Assert.Single(runner.Invocations).StandardInput!;
        Assert.Contains("[TOOL CALL id=c1 name=lookup]", input, StringComparison.Ordinal);
        Assert.Contains("[TOOL RESULT id=c1 status=ok]", input, StringComparison.Ordinal);
    }

    private static LlmChatMessage[] ToolHistory() =>
    [
        new LlmChatMessage("user", "Population of Lyon?"),
        new LlmChatMessage("assistant", string.Empty) { ToolCalls = [new LlmToolCall("c1", "lookup", """{"city":"Lyon"}""")] },
        new LlmChatMessage("user", string.Empty) { ToolResults = [new LlmToolResult("c1", LlmToolOutput.FromText("522,250"))] },
        new LlmChatMessage("user", "Round it."),
    ];

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.GoogleAntigravityCli,
        Endpoint = "agy"
    };

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (T item in source)
        {
            results.Add(item);
        }
        return results;
    }

    private sealed class RecordingRunner(AntigravityCliProcessResult result) : IAntigravityCliRunner
    {
        public List<AntigravityCliInvocation> Invocations { get; } = [];

        public string? SchemaContent { get; private set; }

        public Task<AntigravityCliProcessResult> RunAsync(
            AntigravityCliInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            int schemaIndex = invocation.Arguments.ToList().IndexOf("--json-schema");
            if (schemaIndex >= 0)
            {
                SchemaContent = File.ReadAllText(invocation.Arguments[schemaIndex + 1]);
            }
            return Task.FromResult(result);
        }
    }

}
