using Darkspyre.Inference.Connect.LLM.Internal;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AntigravityCliLlmClientTests
{

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
