using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GeminiArgumentStreamTests
{
    [Fact]
    public async Task IndexedBooleanArgumentsBecomePortableCompletedToolCalls()
    {
        using var http = new HttpClient(new Handler());
        var codec = new GeminiContinuationCodec("test-vertex", "Vertex");
        var route = new GeminiRoute(LlmTransport.GoogleVertexAi, "Vertex", "vertex_",
            (response, token) => ProviderHttp.ThrowIfUnsuccessfulAsync(response, LlmTransport.GoogleVertexAi,
                null, "Vertex generation", token), codec.Encode);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = LlmTransport.GoogleVertexAi,
            Endpoint = "https://vertex.example.test",
            ResolvedContinuationScope = "test-scope"
        };
        var request = new LlmChatRequest
        {
            ModelId = "gemini-3-pro",
            Tools = [new LlmToolDefinition
            {
                Name = "set_flags",
                InputSchema = JsonElement.Parse("""
                    {"type":"object","properties":{"flags":{"type":"array","items":{"type":"boolean"}}}}
                    """)
            }]
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in GeminiStream.ReadAsync(http,
            new(HttpMethod.Post, "https://vertex.example.test/generate"), route, connection, request,
            null, stream: true, usageDeltas: false, reasoningText: false, CancellationToken.None))
            events.Add(item);

        Assert.Equal(3, events.Count);
        Assert.Equal("call-1", Assert.IsType<LlmToolCallStartedEvent>(events[0]).ToolCallId);
        Assert.Equal("call-1", Assert.IsType<LlmToolCallCompletedEvent>(events[1]).ToolCall.Id);
        LlmChatResult result = Assert.IsType<LlmChatCompletionEvent>(events[2]).Result;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(result, ConnectLlmJson.ChatResult);
        LlmChatResult restored = JsonSerializer.Deserialize(json, ConnectLlmJson.ChatResult)!;

        Assert.Equal(LlmCompletionStatus.RequiresAction, restored.Status);
        Assert.Equal("gemini-3-pro-reported", restored.ModelId);
        Assert.Null(restored.Usage);
        LlmToolCall call = Assert.IsType<LlmToolCall>(Assert.Single(restored.ToolCalls));
        Assert.Equal("call-1", call.Id);
        Assert.Equal("set_flags", call.Name);
        using JsonDocument arguments = JsonDocument.Parse(call.ArgumentsJson);
        JsonElement flags = arguments.RootElement.GetProperty("flags");
        Assert.Equal(1, flags.GetArrayLength());
        Assert.True(flags[0].GetBoolean());
        var visible = new LlmChatMessage("assistant", restored.Text)
        {
            ToolCalls = restored.ToolCalls,
            ContinuationState = restored.ContinuationState
        };
        JsonNode? part = Assert.Single(codec.DecodeMessage(visible, connection, request.ModelId));
        Assert.Equal("opaque-signature", part!["thoughtSignature"]!.GetValue<string>());
        Assert.True(part["functionCall"]!["args"]!["flags"]![0]!.GetValue<bool>());
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string body = """
                data: {"candidates":[{"content":{"parts":[{"thoughtSignature":"opaque-signature","functionCall":{"id":"call-1","name":"set_flags","partialArgs":[{"jsonPath":"$.flags[0]","boolValue":true}],"willContinue":true}}]}}]}

                data: {"modelVersion":"gemini-3-pro-reported","candidates":[{"finishReason":"STOP","content":{"parts":[{"functionCall":{"willContinue":false}}]}}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body + "\n\n", Encoding.UTF8, "text/event-stream")
            });
        }
    }
}
