using Darkspyre.Inference.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderTerminalEvidenceTests
{
    [Fact]
    public void UnknownAnnotationsDoNotDisableKnownConstraintValidation()
    {
        var output = new LlmStructuredOutputRequest
        {
            Name = "answer",
            Schema = JsonElement.Parse("""{"type":"object","properties":{"value":{"type":"integer","$comment":"retained"}},"required":["value"],"additionalProperties":false}""")
        };

        LlmStructuredOutputValidation result = StructuredOutput.Validate("""{"value":"wrong-type"}""",
            output, LlmStructuredOutputEnforcement.ProviderJsonSchema);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void CacheCreationStillRejectsBothExpirationFields()
    {
        var request = new LlmContextCacheCreate
        {
            ModelId = "gemini-2.5-flash", Correlation = new("operation", "explicit-correlation"),
            TimeToLive = TimeSpan.FromMinutes(1), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
        };
        Assert.Throws<ArgumentException>(() => GoogleCacheData.Snapshot(request, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(LlmTransport.OpenAiCodex, true, false)]
    [InlineData(LlmTransport.OpenAiCodex, true, true)]
    public async Task ResponsesProtocolFailuresRedactEffectiveRequestSecrets(LlmTransport transport, bool streaming, bool providerError)
    {
        const string error = "{\"type\":\"error\",\"error\":{\"code\":\"rate_limit_test-token_header-value_default-value_provider-secret\",\"message\":\"Failure test-token header-value default-value provider-secret\",\"refresh_token\":\"provider-secret\"}}";
        string payload = providerError ? error : "not-json test-token header-value default-value";
        using var handler = new Handler(transport, streaming, streaming ? $"data: {payload}\n\n" : payload,
            requestId: "request-test-token-header-value-default-value-1");
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("X-Default", "default-value");
        var client = new OpenAiCodexLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = transport,
            Endpoint = transport == LlmTransport.XaiResponses ? "https://api.x.ai/v1" : "https://provider.example.test/v1",
            Xai = transport == LlmTransport.XaiResponses ? new() : null,
            Credential = (transport == LlmTransport.OpenAiResponses
                ? new LlmCredential { ApiKey = "test-token" }
                : new LlmCredential { OAuth = new() { AccessToken = "test-token", AccountId = "test-account" } }).Serialize(),
            AdditionalHeaders = new Dictionary<string, string> { ["X-Trace"] = "header-value" },
            CapabilityOverrides = new() { SupportsStreaming = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported) }
        };
        var observations = new List<LlmStreamEvent>();

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            await foreach (LlmStreamEvent item in client.StreamAsync(connection,
                new() { ModelId = transport == LlmTransport.XaiResponses ? "grok-4.7" : "selected-model", Messages = [new("user", "hello")] }))
                observations.Add(item);
        });

        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        Assert.Equal("request-[REDACTED]-[REDACTED]-[REDACTED]-1", failure.RequestId);
        if (providerError)
            Assert.Equal("rate_limit_[REDACTED]_[REDACTED]_[REDACTED]_[REDACTED]", failure.ProviderCode);
        else
            Assert.Null(failure.ProviderCode);
        foreach (string secret in new[] { "test-token", "header-value", "default-value", "provider-secret" })
            Assert.DoesNotContain(secret, failure.ToString() + failure.SanitizedResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(observations, item => item is LlmChatCompletionEvent);
        Assert.Equal("header-value", handler.RequestTrace);
        Assert.Equal("default-value", handler.RequestDefaultTrace);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(LlmTransport.OpenAiCodex, false, false)]
    [InlineData(LlmTransport.OpenAiCodex, false, true)]
    [InlineData(LlmTransport.OpenAiCodex, true, false)]
    public async Task DiscoveryProtocolFailuresKeepSafeHttpEvidence(LlmTransport transport, bool accountUsage, bool invalidCatalog)
    {
        using var handler = new Handler(transport, streaming: false,
            invalidCatalog ? "{\"models\":null,\"message\":\"test-token header-value default-value\"}" : "not-json test-token header-value default-value",
            requestId: "request-test-token-header-value-default-value-1", expectedMethod: HttpMethod.Get);
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("X-Default", "default-value");
        var client = new OpenAiCodexLlmClient(http);
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = transport,
            Endpoint = accountUsage && transport == LlmTransport.OpenAiCompatibleChat
                ? "https://openrouter.ai/api/v1" : "https://provider.example.test/backend-api/codex",
            Credential = (transport == LlmTransport.OpenAiCodex
                ? new LlmCredential { OAuth = new() { AccessToken = "test-token", AccountId = "test-account" } }
                : new LlmCredential { ApiKey = "test-token" }).Serialize(),
            AdditionalHeaders = new Dictionary<string, string> { ["X-Trace"] = "header-value" }
        };

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            if (accountUsage)
                await ((ILlmAccountUsageClient)client).GetAccountUsageAsync(connection);
            else
                await client.ListModelsAsync(connection);
        });

        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        Assert.Equal("request-[REDACTED]-[REDACTED]-[REDACTED]-1", failure.RequestId);
        Assert.Null(failure.ProviderCode);
        if (invalidCatalog)
        {
            Assert.Equal("Codex model discovery did not return a model catalog.", failure.Message);
            Assert.Null(failure.InnerException);
        }
        else
        {
            Assert.Contains("invalid JSON", failure.Message, StringComparison.Ordinal);
            Assert.IsAssignableFrom<JsonException>(failure.InnerException);
        }
        foreach (string secret in new[] { "test-token", "header-value", "default-value" })
            Assert.DoesNotContain(secret, failure.ToString() + failure.SanitizedResponseBody, StringComparison.Ordinal);
        Assert.Equal("header-value", handler.RequestTrace);
        Assert.Equal("default-value", handler.RequestDefaultTrace);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void OriginalSchemaValidationKeepsRepeatedIdsRequestLocal()
    {
        var first = new LlmStructuredOutputRequest
        {
            Name = "answer",
            Schema = JsonElement.Parse("""
                {"$id":"https://schemas.example.test/repeated-validation","type":"string","maxLength":1}
                """)
        };
        var second = first with
        {
            Schema = JsonElement.Parse("""
                {"$id":"https://schemas.example.test/repeated-validation","type":"string","maxLength":2}
                """)
        };
        const LlmStructuredOutputEnforcement mode = LlmStructuredOutputEnforcement.LocalValidationOnly;
        Assert.False(StructuredOutput.Validate("\"xx\"", first, mode).IsValid);
        Assert.True(StructuredOutput.Validate("\"xx\"", second, mode).IsValid);
        Assert.False(StructuredOutput.Validate("\"xx\"", first, mode).IsValid);
    }

    [Theory]
    [InlineData(LlmTransport.OpenAiCodex, true)]
    public async Task UnknownTerminalToolCallsRemainEvidenceNotSuccessfulContinuation(LlmTransport transport, bool streaming)
    {
        using var http = new HttpClient(new Handler(transport, streaming));
        var connection = new LlmConnection
        {
            Version = "1",
            Transport = transport,
            Endpoint = transport == LlmTransport.XaiResponses ? "https://api.x.ai/v1" : "https://provider.example.test/v1",
            Xai = transport == LlmTransport.XaiResponses ? new() : null,
            Credential = new LlmCredential
            {
                ApiKey = transport is LlmTransport.OpenAiCodex or LlmTransport.XaiResponses ? null : "test-key",
                OAuth = transport is LlmTransport.OpenAiCodex or LlmTransport.XaiResponses
                    ? new() { AccessToken = "test-token", AccountId = "test-account" } : null
            }.Serialize(),
            ResolvedContinuationScope = transport is LlmTransport.GoogleGeminiApi or LlmTransport.GoogleVertexAi ? "test-scope" : null,
            CapabilityOverrides = new() { SupportsStreaming = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported) }
        };
        var request = new LlmChatRequest
        {
            ModelId = transport == LlmTransport.AnthropicMessages ? "claude-sonnet-4-6" : "selected-model",
            MaxOutputTokens = transport == LlmTransport.OpenAiCodex ? null : 16,
            Messages = [new("user", "hello")],
            Tools = [new LlmToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("{\"type\":\"object\",\"additionalProperties\":false}") }]
        };
        var client = new OpenAiCodexLlmClient(http);
        IAsyncEnumerable<LlmStreamEvent> source = client.StreamAsync(connection, request);
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

    private sealed class Handler(LlmTransport transport, bool streaming, string? scriptedBody = null,
        HttpStatusCode statusCode = HttpStatusCode.OK, TimeSpan? retryAfter = null, string? requestId = null,
        HttpMethod? expectedMethod = null, bool emptyCatalog = false, Exception? sendFailure = null) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal Uri? RequestUri { get; private set; }
        internal string? RequestBody { get; private set; }
        internal string? RequestTrace { get; private set; }
        internal string? RequestDefaultTrace { get; private set; }
        internal string? RequestBearerParameter { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestUri = request.RequestUri;
            if (emptyCatalog && request.Method == HttpMethod.Get)
            {
                Assert.True(request.RequestUri!.AbsolutePath is "/v1/models" or "/v1/language-models");
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[],\"models\":[]}", Encoding.UTF8, "application/json") };
            }
            Assert.Equal(expectedMethod ?? HttpMethod.Post, request.Method);
            RequestTrace = request.Headers.TryGetValues("X-Trace", out IEnumerable<string>? values) ? Assert.Single(values) : null;
            RequestDefaultTrace = request.Headers.TryGetValues("X-Default", out IEnumerable<string>? defaults) ? Assert.Single(defaults) : null;
            RequestBearerParameter = request.Headers.Authorization?.Parameter;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (sendFailure is not null) throw sendFailure;
            string body = scriptedBody ?? Body(transport, streaming);
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, streaming && statusCode == HttpStatusCode.OK ? "text/event-stream" : "application/json")
            };
            if (retryAfter is { } delay) response.Headers.RetryAfter = new(delay);
            if (requestId is not null) response.Headers.Add("x-request-id", requestId);
            return response;
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
            LlmTransport.OpenAiResponses or LlmTransport.XaiResponses => responses,
            LlmTransport.OpenAiCompatibleChat => chat,
            LlmTransport.AnthropicMessages => anthropic,
            LlmTransport.GoogleGeminiApi or LlmTransport.GoogleVertexAi => gemini,
            _ => throw new InvalidOperationException("Unexpected complete test route.")
        };
        return transport switch
        {
            LlmTransport.OpenAiResponses or LlmTransport.OpenAiCodex or LlmTransport.XaiResponses =>
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
