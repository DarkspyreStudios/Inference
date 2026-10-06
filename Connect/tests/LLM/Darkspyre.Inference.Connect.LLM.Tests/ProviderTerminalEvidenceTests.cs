using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderTerminalEvidenceTests
{
    [Theory]
    [InlineData(LlmTransport.GoogleGeminiApi)]
    [InlineData(LlmTransport.GoogleVertexAi)]
    public async Task GeminiTerminalUsageReplacesEarlierCountsWithoutCarryingStaleSubsets(LlmTransport transport)
    {
        const string body = """
            data: {"candidates":[{"content":{"parts":[{"text":"hel"}]}}],"usageMetadata":{"promptTokenCount":5,"toolUsePromptTokenCount":3,"candidatesTokenCount":2,"thoughtsTokenCount":4,"cachedContentTokenCount":1,"totalTokenCount":14}}

            data: {"candidates":[{"content":{"parts":[{"text":"lo"}]}}]}

            data: {"candidates":[{"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":6,"candidatesTokenCount":3,"totalTokenCount":9}}
            """;
        using var http = new HttpClient(new Handler(transport, streaming: true, body + "\n\n"));
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = transport,
            Endpoint = "https://provider.example.test/v1",
            ResolvedContinuationScope = "test-scope"
        };
        var request = new LlmChatRequest { ModelId = "gemini-2.5-flash", Messages = [new("user", "hello")] };
        var codec = new GeminiContinuationCodec("test-gemini", "Gemini");
        var route = new GeminiRoute(transport, "Gemini", "gemini_",
            (response, token) => ProviderHttp.ThrowIfUnsuccessfulAsync(response, transport, null, "generation", token), codec.Encode);
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in GeminiStream.ReadAsync(http, new(HttpMethod.Post, connection.Endpoint), route,
            connection, request, null, stream: true, usageDeltas: true, reasoningText: false, CancellationToken.None))
            events.Add(item);

        LlmUsage[] observations = events.OfType<LlmUsageDeltaEvent>().Select(item => item.Cumulative).ToArray();
        Assert.Equal(2, observations.Length);
        Assert.Equal(new LlmUsage(8, 6, 14) { CachedInputTokens = 1, ReasoningOutputTokens = 4 }, observations[0]);
        var corrected = new LlmUsage(6, 3, 9);
        Assert.Equal(corrected, observations[1]);
        LlmChatResult terminal = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        LlmChatResult restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(terminal, ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;
        Assert.Equal(LlmCompletionStatus.Completed, restored.Status);
        Assert.Equal("hello", restored.Text);
        Assert.Equal(corrected, restored.Usage);
        Assert.NotNull(restored.ContinuationState);
    }

    [Theory]
    [InlineData(LlmTransport.OpenAiResponses, true)]
    [InlineData(LlmTransport.OpenAiResponses, false)]
    [InlineData(LlmTransport.OpenAiCompatibleChat, true)]
    [InlineData(LlmTransport.OpenAiCompatibleChat, false)]
    [InlineData(LlmTransport.AnthropicMessages, true)]
    [InlineData(LlmTransport.AnthropicMessages, false)]
    [InlineData(LlmTransport.GoogleGeminiApi, true)]
    [InlineData(LlmTransport.GoogleGeminiApi, false)]
    [InlineData(LlmTransport.GoogleVertexAi, true)]
    [InlineData(LlmTransport.GoogleVertexAi, false)]
    [InlineData(LlmTransport.OpenAiCodex, true)]
    public async Task UnknownTerminalToolCallsRemainEvidenceNotSuccessfulContinuation(LlmTransport transport, bool streaming)
    {
        using var http = new HttpClient(new Handler(transport, streaming));
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = transport,
            Endpoint = "https://provider.example.test/v1",
            Credential = new LlmCredential
            {
                ApiKey = transport == LlmTransport.OpenAiCodex ? null : "test-key",
                OAuth = transport == LlmTransport.OpenAiCodex ? new() { AccessToken = "test-token", AccountId = "test-account" } : null
            }.Serialize(),
            ResolvedContinuationScope = transport is LlmTransport.GoogleGeminiApi or LlmTransport.GoogleVertexAi ? "test-scope" : null,
            CapabilityOverrides = new() { SupportsStreaming = streaming }
        };
        var request = new LlmChatRequest
        {
            ModelId = transport == LlmTransport.AnthropicMessages ? "claude-sonnet-4-6" : "selected-model",
            MaxOutputTokens = transport == LlmTransport.OpenAiCodex ? null : 16,
            Messages = [new("user", "hello")],
            Tools = [new LlmToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\",\"additionalProperties\":false}") }]
        };
        IAsyncEnumerable<LlmStreamEvent> source;
        if (transport is LlmTransport.GoogleGeminiApi or LlmTransport.GoogleVertexAi)
        {
            var codec = new GeminiContinuationCodec("test-gemini", "Gemini");
            var route = new GeminiRoute(transport, "Gemini", "gemini_",
                (response, token) => ProviderHttp.ThrowIfUnsuccessfulAsync(response, transport, null, "generation", token), codec.Encode);
            source = GeminiStream.ReadAsync(http, new(HttpMethod.Post, connection.Endpoint), route, connection,
                request, null, streaming, usageDeltas: true, reasoningText: false, CancellationToken.None);
        }
        else
        {
            ILlmClient client = transport switch
            {
                LlmTransport.OpenAiResponses => new OpenAiResponsesLlmClient(http),
                LlmTransport.OpenAiCodex => new OpenAiCodexLlmClient(http),
                LlmTransport.OpenAiCompatibleChat => new OpenAiCompatibleChatLlmClient(http),
                LlmTransport.AnthropicMessages => new AnthropicMessagesLlmClient(http),
                _ => throw new InvalidOperationException("Unexpected test route.")
            };
            source = client.StreamAsync(connection, request);
        }
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in source) events.Add(item);
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        Assert.IsType<LlmChatCompletionEvent>(events[^1]);
        LlmChatResult restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(result, ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;

        Assert.Equal(LlmCompletionStatus.Unknown, restored.Status);
        Assert.Equal("UNKNOWN_STOP", restored.FinishReason);
        Assert.Equal("partial", restored.Text);
        Assert.Equal("reported-model", restored.ModelId);
        Assert.Equal(new LlmUsage(5, 2, 7), restored.Usage);
        Assert.Null(restored.ContinuationState);
        LlmToolCall call = Assert.IsType<LlmToolCall>(Assert.Single(restored.ToolCalls));
        Assert.Equal("call-1", call.Id);
        Assert.Equal("echo", call.Name);
        Assert.Equal("{}", call.ArgumentsJson);
        Assert.Equal(call.Id, Assert.Single(events.OfType<LlmToolCallStartedEvent>()).ToolCallId);
        Assert.Equal(call.Id, Assert.Single(events.OfType<LlmToolCallCompletedEvent>()).ToolCall.Id);
    }

    private sealed class Handler(LlmTransport transport, bool streaming, string? scriptedBody = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            string body = scriptedBody ?? Body(transport, streaming);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            });
        }
    }

    private static string Body(LlmTransport transport, bool streaming)
    {
        const string responses = """
            {"id":"response-1","model":"reported-model","status":"UNKNOWN_STOP","output":[{"type":"message","id":"message-1","role":"assistant","content":[{"type":"output_text","text":"partial"}]},{"type":"function_call","id":"fc-1","call_id":"call-1","name":"echo","arguments":"{}"}],"usage":{"input_tokens":5,"output_tokens":2,"total_tokens":7}}
            """;
        const string chat = """
            {"id":"response-1","model":"reported-model","choices":[{"index":0,"message":{"role":"assistant","content":"partial","tool_calls":[{"id":"call-1","type":"function","function":{"name":"echo","arguments":"{}"}}]},"finish_reason":"UNKNOWN_STOP"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}
            """;
        const string anthropic = """
            {"type":"message","id":"response-1","model":"reported-model","content":[{"type":"text","text":"partial"},{"type":"tool_use","id":"call-1","name":"echo","input":{}}],"stop_reason":"UNKNOWN_STOP","usage":{"input_tokens":5,"output_tokens":2}}
            """;
        const string gemini = """
            {"responseId":"response-1","modelVersion":"reported-model","candidates":[{"finishReason":"UNKNOWN_STOP","content":{"parts":[{"text":"partial"},{"functionCall":{"id":"call-1","name":"echo","args":{}}}]}}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":2,"totalTokenCount":7}}
            """;
        if (!streaming) return transport switch
        {
            LlmTransport.OpenAiResponses => responses,
            LlmTransport.OpenAiCompatibleChat => chat,
            LlmTransport.AnthropicMessages => anthropic,
            LlmTransport.GoogleGeminiApi or LlmTransport.GoogleVertexAi => gemini,
            _ => throw new InvalidOperationException("Unexpected complete test route.")
        };
        return transport switch
        {
            LlmTransport.OpenAiResponses or LlmTransport.OpenAiCodex =>
                "data: {\"type\":\"response.completed\",\"response\":" + responses + "}\n\n",
            LlmTransport.GoogleGeminiApi or LlmTransport.GoogleVertexAi => "data: " + gemini + "\n\n",
            LlmTransport.OpenAiCompatibleChat => """
                data: {"id":"response-1","model":"reported-model","choices":[{"index":0,"delta":{"content":"partial","tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"echo","arguments":"{}"}}]},"finish_reason":"UNKNOWN_STOP"}],"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7}}

                data: [DONE]
                """ + "\n\n",
            LlmTransport.AnthropicMessages => """
                data: {"type":"message_start","message":{"id":"response-1","model":"reported-model","usage":{"input_tokens":5,"output_tokens":0}}}

                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":"partial"}}

                data: {"type":"content_block_stop","index":0}

                data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"call-1","name":"echo","input":{}}}

                data: {"type":"content_block_stop","index":1}

                data: {"type":"message_delta","delta":{"stop_reason":"UNKNOWN_STOP"},"usage":{"output_tokens":2}}

                data: {"type":"message_stop"}
                """ + "\n\n",
            _ => throw new InvalidOperationException("Unexpected streamed test route.")
        };
    }
}
