using Darkspyre.Inference.Connect.LLM.Internal;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ClaudeCliLlmClientTests
{
    [Fact]
    public async Task ToolHistoryReachesThePromptAsLabelledBlocks()
    {
        var runner = new RecordingRunner(new ClaudeCliProcessResult(
            0,
            """
            {"type":"system","subtype":"init","session_id":"session-1"}
            {"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"522,000"}}}
            {"type":"result","subtype":"success","is_error":false,"result":"522,000","session_id":"session-1","stop_reason":"end_turn","terminal_reason":"completed","usage":{"input_tokens":7,"output_tokens":2}}
            """,
            string.Empty));
        using var http = new HttpClient(new UnexpectedHttpHandler());
        var client = new ClaudeCliLlmClient(http, runner, new NoSessionProcesses());

        IReadOnlyList<LlmStreamEvent> events = await ReadAllAsync(client.StreamAsync(
            CliConnection(), new LlmChatRequest { ModelId = "claude-test", Messages = ToolHistory() }));

        Assert.Equal("522,000", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
        string input = Assert.Single(runner.Invocations).StandardInput!;
        Assert.Contains("[TOOL CALL id=c1 name=lookup]", input, StringComparison.Ordinal);
        Assert.Contains("[TOOL RESULT id=c1 status=ok]", input, StringComparison.Ordinal);
        Assert.Equal(LlmToolHistory.Text, client.GetCapabilities(CliConnection()).ToolHistory);
    }

    private static LlmChatMessage[] ToolHistory() =>
    [
        new LlmChatMessage("user", "Population of Lyon?"),
        new LlmChatMessage("assistant", string.Empty) { ToolCalls = [new LlmToolCall("c1", "lookup", """{"city":"Lyon"}""")] },
        new LlmChatMessage("user", string.Empty) { ToolResults = [new LlmToolResult("c1", LlmToolOutput.FromText("522,250"))] },
        new LlmChatMessage("user", "Round it."),
    ];

    private static LlmConnection CliConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AnthropicClaudeCli,
        Endpoint = "claude",
        Credential = null
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

    private sealed class RecordingRunner(ClaudeCliProcessResult result) : IClaudeCliRunner
    {
        public List<ClaudeCliInvocation> Invocations { get; } = [];
        public Action<ClaudeCliInvocation>? OnInvocation { get; init; }

        public Task<ClaudeCliProcessResult> RunAsync(
            ClaudeCliInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            OnInvocation?.Invoke(invocation);
            return Task.FromResult(result);
        }
    }

    private sealed class UnexpectedHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("OAuth prompt unexpectedly used HTTP.");
    }

    private sealed class NoSessionProcesses : IClaudeCliSessionProcessFactory
    {
        public Task<IClaudeCliSessionProcess> StartAsync(
            ClaudeCliSessionInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
