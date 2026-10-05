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

    private sealed class Handler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
            using JsonDocument payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
            const string body = """
                data: {"type":"message_start","message":{"id":"message-1","model":"claude-sonnet-4-6-reported","usage":{"input_tokens":6,"output_tokens":1,"cache_read_input_tokens":3,"cache_creation_input_tokens":2}}}

                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":"hello"}}

                data: {"type":"content_block_stop","index":0}

                data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":4}}

                data: {"type":"message_stop"}
                """;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(body + "\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
