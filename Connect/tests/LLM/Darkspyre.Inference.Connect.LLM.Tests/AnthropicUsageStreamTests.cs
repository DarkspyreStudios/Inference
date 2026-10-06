using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AnthropicUsageStreamTests
{
    [Fact]
    public async Task InitialOutputUsageIsObservedAndLaterUsageReplacesIt()
    {
        using var http = new HttpClient(new Handler());
        var client = new AnthropicMessagesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.AnthropicMessages,
            Endpoint = "https://anthropic.example.test/v1",
            Credential = new LlmCredential { ApiKey = "test-key" }.Serialize()
        };
        var request = new LlmChatRequest
        {
            ModelId = "claude-sonnet-4-6",
            MaxOutputTokens = 16,
            Messages = [new("user", "hello")]
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);

        Assert.Equal(4, events.Count);
        LlmUsage initial = Assert.IsType<LlmUsageDeltaEvent>(events[0]).Cumulative;
        Assert.Equal(11, initial.InputTokens);
        Assert.Equal(1, initial.OutputTokens);
        Assert.Equal(12, initial.TotalTokens);
        Assert.Equal(3, initial.CachedInputTokens);
        Assert.Equal(2, initial.CacheWriteInputTokens);
        Assert.Equal("hello", Assert.IsType<LlmTextDeltaEvent>(events[1]).Delta);
        LlmUsage updated = Assert.IsType<LlmUsageDeltaEvent>(events[2]).Cumulative;
        Assert.Equal(4, updated.OutputTokens);
        Assert.Equal(15, updated.TotalTokens);

        LlmChatResult result = Assert.IsType<LlmChatCompletionEvent>(events[3]).Result;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(result, ConnectLlmJson.ChatResult);
        LlmChatResult restored = JsonSerializer.Deserialize(json, ConnectLlmJson.ChatResult)!;
        Assert.Equal(LlmCompletionStatus.Completed, restored.Status);
        Assert.Equal("end_turn", restored.FinishReason);
        Assert.Equal("claude-sonnet-4-6-reported", restored.ModelId);
        LlmUsage terminal = Assert.IsType<LlmUsage>(restored.Usage);
        Assert.Equal(11, terminal.InputTokens);
        Assert.Equal(4, terminal.OutputTokens);
        Assert.Equal(15, terminal.TotalTokens);
        Assert.Equal(3, terminal.CachedInputTokens);
        Assert.Equal(2, terminal.CacheWriteInputTokens);
    }

    [Theory]
    [InlineData("max_tokens", LlmCompletionStatus.Incomplete)]
    [InlineData("UNKNOWN_STOP", LlmCompletionStatus.Unknown)]
    [InlineData("tool_use", LlmCompletionStatus.RequiresAction)]
    public async Task ToolArgumentsDoNotHideTheActualTerminalOutcome(string stopReason, LlmCompletionStatus status)
    {
        string arguments = status == LlmCompletionStatus.RequiresAction ? "{\"value\":1}" : "{\"value\":";
        string body = $$$$"""
            data: {"type":"message_start","message":{"id":"message-1","model":"claude-sonnet-4-6-reported","usage":{"input_tokens":5,"output_tokens":0}}}

            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call-1","name":"echo","input":{}}}

            data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{{{{JsonEncodedText.Encode(arguments)}}}}"}}

            data: {"type":"content_block_stop","index":0}

            data: {"type":"message_delta","delta":{"stop_reason":"{{{{stopReason}}}}"},"usage":{"output_tokens":2}}

            data: {"type":"message_stop"}
            """;
        using var http = new HttpClient(new Handler(body));
        var client = new AnthropicMessagesLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.AnthropicMessages,
            Endpoint = "https://anthropic.example.test/v1",
            Credential = new LlmCredential { ApiKey = "test-key" }.Serialize(),
            ResolvedContinuationScope = "test-scope",
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["anthropic-beta"] = "fine-grained-tool-streaming-2025-05-14"
            }
        };
        var request = new LlmChatRequest
        {
            ModelId = "claude-sonnet-4-6",
            MaxOutputTokens = 16,
            Messages = [new("user", "hello")],
            Tools = [new LlmToolDefinition
            {
                Name = "echo",
                InputSchema = JsonElement.Parse("""{"type":"object","properties":{"value":{"type":"integer"}},"additionalProperties":false}""")
            }]
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);

        Assert.Equal(6, events.Count);
        Assert.Equal(new LlmUsage(5, 0, 5), Assert.IsType<LlmUsageDeltaEvent>(events[0]).Cumulative);
        Assert.Equal("call-1", Assert.IsType<LlmToolCallStartedEvent>(events[1]).ToolCallId);
        Assert.Equal(new LlmToolCallArgumentsDeltaEvent("call-1", arguments), Assert.IsType<LlmToolCallArgumentsDeltaEvent>(events[2]));
        var call = new LlmToolCall("call-1", "echo", arguments);
        Assert.Equal(call, Assert.IsType<LlmToolCallCompletedEvent>(events[3]).ToolCall);
        Assert.Equal(new LlmUsage(5, 2, 7), Assert.IsType<LlmUsageDeltaEvent>(events[4]).Cumulative);
        LlmChatResult result = Assert.IsType<LlmChatCompletionEvent>(events[5]).Result;
        LlmChatResult restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(result, ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;

        Assert.Equal(status, restored.Status);
        Assert.Equal(stopReason, restored.FinishReason);
        Assert.Equal(status == LlmCompletionStatus.Incomplete ? stopReason : null, restored.IncompleteReason);
        Assert.Equal("message-1", restored.ProviderResponseId);
        Assert.Equal("claude-sonnet-4-6-reported", restored.ModelId);
        Assert.Equal(new LlmUsage(5, 2, 7), restored.Usage);
        if (status == LlmCompletionStatus.Incomplete) Assert.Empty(restored.ToolCalls);
        else Assert.Equal(call, Assert.Single(restored.ToolCalls));
        if (status == LlmCompletionStatus.RequiresAction)
        {
            var (content, containerId) = AnthropicContinuation.ReadPayload(Assert.IsType<LlmContinuationState>(restored.ContinuationState).Payload);
            Assert.Null(containerId);
            Assert.Single(content);
            Assert.Equal("call-1", content[0]!["id"]!.GetValue<string>());
            Assert.Equal("echo", content[0]!["name"]!.GetValue<string>());
            Assert.Equal(1, content[0]!["input"]!["value"]!.GetValue<int>());
        }
        else Assert.Null(restored.ContinuationState);
    }

    private sealed class Handler(string? scriptedBody = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
            using JsonDocument payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
            if (scriptedBody is not null)
            {
                Assert.Equal("fine-grained-tool-streaming-2025-05-14", Assert.Single(request.Headers.GetValues("anthropic-beta")));
                Assert.Equal("echo", payload.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());
            }
            const string body = """
                data: {"type":"message_start","message":{"id":"message-1","model":"claude-sonnet-4-6-reported","usage":{"input_tokens":6,"output_tokens":1,"cache_read_input_tokens":3,"cache_creation_input_tokens":2}}}

                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":"hello"}}

                data: {"type":"content_block_stop","index":0}

                data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":4}}

                data: {"type":"message_stop"}
                """;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent((scriptedBody ?? body) + "\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
