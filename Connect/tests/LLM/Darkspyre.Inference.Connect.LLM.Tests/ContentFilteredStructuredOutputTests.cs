using Darkspyre.Inference.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ContentFilteredStructuredOutputTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FilteredPartialJsonIsNotValidatedAsAnAnswer(bool streaming)
    {
        using var http = new HttpClient(new Handler(streaming));
        var client = new OpenAiCompatibleChatLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.OpenAiCompatibleChat,
            Endpoint = "https://compatible.example.test/v1",
            Credential = new LlmCredential { ApiKey = "test-key" }.Serialize(),
            CapabilityOverrides = new() { SupportsStreaming = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported), SupportsJsonSchema = InferenceSupport.Supported }
        };
        var request = new LlmChatRequest
        {
            ModelId = "selected-model",
            Messages = [new("user", "hello")],
            StructuredOutput = new()
            {
                Name = "reply",
                Schema = JsonElement.Parse("""
                    {"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}
                    """)
            }
        };
        var events = new List<LlmStreamEvent>();
        await foreach (var item in client.StreamAsync(connection, request)) events.Add(item);
        LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result, ConnectLlmJson.ChatResult);
        LlmChatResult restored = JsonSerializer.Deserialize(bytes, ConnectLlmJson.ChatResult)!;

        Assert.Equal(LlmCompletionStatus.Refused, restored.Status);
        Assert.Equal("content_filter", restored.FinishReason);
        Assert.Equal("{\"value\":\"partial\"}", restored.Text);
        Assert.Null(restored.Refusal);
        Assert.Equal("reported-model", restored.ModelId);
        Assert.Equal(7, restored.Usage!.TotalTokens);
        var validation = Assert.IsType<LlmStructuredOutputValidation>(restored.StructuredOutput);
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, validation.Enforcement);
        Assert.False(validation.WasEvaluated);
        Assert.False(validation.IsValid);
        Assert.Equal("Structured output was not evaluated because the provider refused the request.",
            Assert.Single(validation.Errors));
    }

    private sealed class Handler(bool streaming) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            using var sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(streaming, sent.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("json_schema", sent.RootElement.GetProperty("response_format").GetProperty("type").GetString());
            const string streamed = """
                data: {"id":"response-1","model":"reported-model","choices":[{"index":0,"delta":{"content":"{\"value\":\"partial\"}"},"finish_reason":null}]}

                data: {"id":"response-1","choices":[{"index":0,"delta":{},"finish_reason":"content_filter"}],"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":7}}

                data: [DONE]
                """;
            const string complete = """
                {"id":"response-1","model":"reported-model","choices":[{"index":0,"message":{"role":"assistant","content":"{\"value\":\"partial\"}"},"finish_reason":"content_filter"}],"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":7}}
                """;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(streaming ? streamed + "\n\n" : complete, Encoding.UTF8,
                    streaming ? "text/event-stream" : "application/json")
            };
        }
    }
}
