using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LlmTransportTests
{
    [Fact]
    public async Task EmbeddingsUseConfiguredProviderEndpointAndPreserveVectorOrder()
    {
        const string body = """
            {"data":[{"index":1,"embedding":[0.3,0.4]},{"index":0,"embedding":[0.1,0.2]}],"usage":{"prompt_tokens":6,"total_tokens":6}}
            """;
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiEmbeddingClient(http);

        LlmEmbeddingResult result = await client.EmbedAsync(
            ResponsesConnection(),
            new LlmEmbeddingRequest
            {
                ModelId = "embedding-model",
                Inputs = ["first", "second"]
            });

        Assert.Equal("https://api.openai.test/v1/embeddings", handler.RequestUri?.AbsoluteUri);
        Assert.Equal([0.1f, 0.2f], result.Vectors[0]);
        Assert.Equal([0.3f, 0.4f], result.Vectors[1]);
        Assert.Equal(6, result.Usage?.InputTokens);
        using JsonDocument sent = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal("embedding-model", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("second", sent.RootElement.GetProperty("input")[1].GetString());
    }

    [Fact]
    public async Task ResponsesStreamsTextAndPreservesRequestAndDiagnostics()
    {
        const string events = """
            data: {"type":"response.output_text.delta","delta":"{\"answer\":"}

            data: {"type":"response.output_text.delta","delta":"\"yes\"}"}

            data: {"type":"response.completed","response":{"id":"resp_123","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"{\"answer\":\"yes\"}"}]}],"usage":{"input_tokens":12,"output_tokens":5,"total_tokens":17}}}

            """;
        var handler = new RecordingHttpMessageHandler(_ => SseResponse(events, "req_123"));
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(http);
        LlmConnection connection = ResponsesConnection() with
        {
            AdditionalHeaders = new Dictionary<string, string> { ["X-Title"] = "ChatEngine" },
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["metadata"] = JsonSerializer.SerializeToElement(new { story = "test" })
            }
        };
        var request = new LlmChatRequest
        {
            ModelId = "gpt-test",
            Instructions = "Return the requested object.",
            Messages = [new LlmChatMessage("user", "Answer yes.")],
            Temperature = 0.4,
            MaxOutputTokens = 100,
            ReasoningEffort = "low",
            StructuredOutput = new LlmStructuredOutputRequest
            {
                Name = "answer",
                Description = "A yes or no answer.",
                Schema = Schema("""
                    {"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}
                    """)
            }
        };

        IReadOnlyList<LlmStreamEvent> stream = await ReadAllAsync(client.StreamAsync(connection, request));

        Assert.Equal(["{\"answer\":", "\"yes\"}"], stream.OfType<LlmTextDeltaEvent>().Select(item => item.Delta));
        LlmChatResult result = Assert.Single(stream.OfType<LlmCompletionEvent>()).Result;
        Assert.Equal("{\"answer\":\"yes\"}", result.Text);
        Assert.Equal(LlmCompletionStatus.Completed, result.Status);
        Assert.Equal("resp_123", result.ProviderResponseId);
        Assert.Equal(17, result.Usage?.TotalTokens);
        Assert.True(result.StructuredOutput?.IsValid);
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, result.StructuredOutput?.Enforcement);
        Assert.Equal("req_123", result.Diagnostics.RequestId);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://api.openai.test/v1/responses", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("sk-test-secret", handler.Authorization?.Parameter);
        Assert.Equal("ChatEngine", Assert.Single(handler.GetHeaderValues("X-Title")));

        using JsonDocument sent = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        JsonElement root = sent.RootElement;
        Assert.Equal("gpt-test", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("Return the requested object.", root.GetProperty("instructions").GetString());
        Assert.Equal("Answer yes.", root.GetProperty("input")[0].GetProperty("content").GetString());
        Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("test", root.GetProperty("metadata").GetProperty("story").GetString());
    }

    [Fact]
    public async Task ResponsesReportsRefusalWithoutTreatingItAsMalformedJson()
    {
        const string events = """
            data: {"type":"response.refusal.delta","delta":"I cannot help."}

            data: {"type":"response.completed","response":{"id":"resp_refused","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"refusal","refusal":"I cannot help."}]}]}}

            """;
        var handler = new RecordingHttpMessageHandler(_ => SseResponse(events));
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(http);
        var request = new LlmChatRequest
        {
            ModelId = "gpt-test",
            Messages = [new LlmChatMessage("user", "Request")],
            StructuredOutput = new LlmStructuredOutputRequest
            {
                Name = "result",
                Schema = Schema("{" + "\"type\":\"object\"}")
            }
        };

        IReadOnlyList<LlmStreamEvent> stream = await ReadAllAsync(
            client.StreamAsync(ResponsesConnection(), request));

        Assert.Equal("I cannot help.", Assert.Single(stream.OfType<LlmRefusalDeltaEvent>()).Delta);
        LlmChatResult result = Assert.Single(stream.OfType<LlmCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Refused, result.Status);
        Assert.Equal("I cannot help.", result.Refusal);
        Assert.False(result.StructuredOutput?.WasEvaluated);
    }

    [Fact]
    public async Task ResponsesRejectsMalformedStreamEvents()
    {
        var handler = new RecordingHttpMessageHandler(_ => SseResponse("data: {not-json}\n\n"));
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(http);

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() => ReadAllAsync(
            client.StreamAsync(
                ResponsesConnection(),
                new LlmChatRequest { ModelId = "gpt-test" })));

        Assert.Contains("malformed stream event", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpErrorsRedactCredentialsAndSensitiveJsonProperties()
    {
        const string body = """
            {"error":{"code":"invalid_api_key","message":"Rejected sk-test-secret and header-secret"},"api_key":"sk-test-secret"}
            """;
        var handler = new RecordingHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            response.Headers.Add("x-request-id", "req_error");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            return response;
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(http);

        LlmConnection connection = ResponsesConnection() with
        {
            Credential = new LlmCredential
            {
                ApiKey = "sk-test-secret",
                SecretHeaders = new Dictionary<string, string> { ["X-Custom-Key"] = "header-secret" }
            }.Serialize()
        };
        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() => ReadAllAsync(
            client.StreamAsync(
                connection,
                new LlmChatRequest { ModelId = "gpt-test" })));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("invalid_api_key", exception.ProviderCode);
        Assert.Equal("req_error", exception.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(10), exception.RetryAfter);
        Assert.DoesNotContain("sk-test-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-test-secret", exception.SanitizedResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret", exception.SanitizedResponseBody, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", exception.SanitizedResponseBody, StringComparison.Ordinal);
        Assert.Equal("header-secret", Assert.Single(handler.GetHeaderValues("X-Custom-Key")));
    }

    [Fact]
    public async Task CompatibleChatStreamsUsageAndNativeSchemaRequest()
    {
        const string events = """
            data: {"id":"chat_123","choices":[{"delta":{"content":"{\"value\":"},"finish_reason":null}]}

            data: {"id":"chat_123","choices":[{"delta":{"content":"3}"},"finish_reason":"stop"}]}

            data: {"id":"chat_123","choices":[],"usage":{"prompt_tokens":9,"completion_tokens":3,"total_tokens":12}}

            data: [DONE]

            """;
        var handler = new RecordingHttpMessageHandler(_ => SseResponse(events));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        LlmConnection connection = CompatibleConnection() with
        {
            CapabilityOverrides = new LlmCapabilityOverrides
            {
                SupportsJsonSchema = true,
                SupportsReasoningEffort = true
            }
        };
        var request = new LlmChatRequest
        {
            ModelId = "router/model",
            Instructions = "Use JSON.",
            Messages = [new LlmChatMessage("user", "Return three.")],
            ReasoningEffort = "medium",
            StructuredOutput = new LlmStructuredOutputRequest
            {
                Name = "value",
                Schema = Schema("""
                    {"type":"object","properties":{"value":{"type":"integer"}},"required":["value"]}
                    """)
            }
        };

        IReadOnlyList<LlmStreamEvent> stream = await ReadAllAsync(client.StreamAsync(connection, request));

        LlmChatResult result = Assert.Single(stream.OfType<LlmCompletionEvent>()).Result;
        Assert.Equal("{\"value\":3}", result.Text);
        Assert.Equal(12, result.Usage?.TotalTokens);
        Assert.True(result.StructuredOutput?.IsValid);
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, result.StructuredOutput?.Enforcement);

        using JsonDocument sent = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        JsonElement root = sent.RootElement;
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("medium", root.GetProperty("reasoning_effort").GetString());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("json_schema", root.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task CompatibleChatUsesOnlyExplicitlyAllowedLocalSchemaFallback()
    {
        const string events = """
            data: {"id":"chat_local","choices":[{"delta":{"content":"{\"value\":\"wrong\"}"},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        var handler = new RecordingHttpMessageHandler(_ => SseResponse(events));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        var request = new LlmChatRequest
        {
            ModelId = "local-model",
            StructuredOutput = new LlmStructuredOutputRequest
            {
                Name = "value",
                Fallback = LlmStructuredOutputFallback.LocalValidation,
                Schema = Schema("""
                    {"type":"object","properties":{"value":{"type":"integer"}},"required":["value"]}
                    """)
            }
        };

        IReadOnlyList<LlmStreamEvent> stream = await ReadAllAsync(
            client.StreamAsync(CompatibleConnection(), request));

        LlmChatResult result = Assert.Single(stream.OfType<LlmCompletionEvent>()).Result;
        Assert.False(result.StructuredOutput?.IsValid);
        Assert.Equal(LlmStructuredOutputEnforcement.LocalValidationOnly, result.StructuredOutput?.Enforcement);
        using JsonDocument sent = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.False(sent.RootElement.TryGetProperty("response_format", out _));
    }

    [Fact]
    public async Task CompatibleChatRejectsUnapprovedStructuredOutputFallbackBeforeSending()
    {
        var handler = new RecordingHttpMessageHandler(_ => SseResponse(string.Empty));
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);
        var request = new LlmChatRequest
        {
            ModelId = "local-model",
            StructuredOutput = new LlmStructuredOutputRequest
            {
                Name = "value",
                Schema = Schema("{" + "\"type\":\"object\"}")
            }
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => ReadAllAsync(
            client.StreamAsync(CompatibleConnection(), request)));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ModelDiscoveryUsesConnectionTransportAndSortsModels()
    {
        const string body = """
            {"data":[{"id":"z-model","owned_by":"vendor"},{"id":"a-model","name":"Alpha"}]}
            """;
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleChatLlmClient(http);

        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(CompatibleConnection());

        Assert.Equal(["a-model", "z-model"], models.Select(model => model.Id));
        Assert.Equal("https://router.test/v1/models", handler.RequestUri?.AbsoluteUri);
    }

    [Fact]
    public async Task AnthropicOAuthModelDiscoveryKeepsTheExistingAuthenticatedHttpPath()
    {
        var handler = new RecordingHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"claude-test","display_name":"Claude Test"}],"has_more":false,"last_id":"claude-test"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var http = new HttpClient(handler);
        var client = new ClaudeCliLlmClient(http, new UnexpectedClaudeRunner());
        var connection = new LlmConnection
        {
            Version = "v1",
            Transport = LlmTransport.AnthropicClaudeCli,
            Endpoint = "claude",
            Credential = new LlmCredential
            {
                OAuth = new LlmOAuthTokens
                {
                    AccessToken = "claude-access",
                    RefreshToken = "claude-refresh"
                }
            }.Serialize()
        };
        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(connection);

        Assert.Equal("claude-test", Assert.Single(models).Id);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("claude-access", handler.Authorization?.Parameter);
        Assert.Equal(
            "claude-code-20250219,oauth-2025-04-20",
            Assert.Single(handler.GetHeaderValues("anthropic-beta")));
        Assert.Equal(
            "claude-cli/2.1.278 (external, cli)",
            string.Join(" ", handler.GetHeaderValues("User-Agent")));
        Assert.Equal("cli", Assert.Single(handler.GetHeaderValues("x-app")));
        Assert.Equal(
            "true",
            Assert.Single(handler.GetHeaderValues("anthropic-dangerous-direct-browser-access")));
        Assert.Equal("application/json", Assert.Single(handler.GetHeaderValues("Accept")));
        Assert.Equal("https://api.anthropic.com/v1/models?limit=1000", handler.RequestUri?.AbsoluteUri);
        Assert.True(Guid.TryParse(
            Assert.Single(handler.GetHeaderValues("X-Claude-Code-Session-Id")),
            out _));
    }

    [Fact]
    public async Task AnthropicRefusalReportsExplanationAndCategoryInsteadOfStreamedText()
    {
        const string events = """
            data: {"type":"message_start","message":{"id":"msg_2","usage":{"input_tokens":11}}}

            data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"Here is how "}}

            data: {"type":"message_delta","delta":{"stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber","explanation":"This request was declined because it could enable cyber harm."}},"usage":{"output_tokens":4}}

            data: {"type":"message_stop"}

            """;
        var handler = new RecordingHttpMessageHandler(_ => SseResponse(events));
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);

        IReadOnlyList<LlmStreamEvent> stream = await ReadAllAsync(
            client.StreamAsync(AnthropicConnection(), AnthropicRequest()));

        Assert.Equal(
            "This request was declined because it could enable cyber harm.",
            Assert.Single(stream.OfType<LlmRefusalDeltaEvent>()).Delta);
        LlmChatResult result = Assert.Single(stream.OfType<LlmCompletionEvent>()).Result;
        Assert.Equal(LlmCompletionStatus.Refused, result.Status);
        Assert.Equal(
            "This request was declined because it could enable cyber harm.",
            result.Refusal);
        Assert.Equal("cyber", result.RefusalCategory);
        Assert.Equal("Here is how ", result.Text);
    }

    [Fact]
    public async Task AnthropicRefusalWithoutOutputCarriesTheExplanationRatherThanEmptyText()
    {
        const string events = """
            data: {"type":"message_start","message":{"id":"msg_3","usage":{"input_tokens":9}}}

            data: {"type":"message_delta","delta":{"stop_reason":"refusal","stop_details":{"type":"refusal","category":null,"explanation":"This request was declined."}},"usage":{"output_tokens":0}}

            data: {"type":"message_stop"}

            """;
        var handler = new RecordingHttpMessageHandler(_ => SseResponse(events));
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);

        IReadOnlyList<LlmStreamEvent> stream = await ReadAllAsync(
            client.StreamAsync(AnthropicConnection(), AnthropicRequest()));

        LlmChatResult result = Assert.Single(stream.OfType<LlmCompletionEvent>()).Result;
        Assert.Equal("This request was declined.", result.Refusal);
        Assert.Null(result.RefusalCategory);
        Assert.Empty(result.Text);
    }

    [Fact]
    public async Task AnthropicRejectsTemperatureUntilTheModelOptsBackIn()
    {
        var handler = new RecordingHttpMessageHandler(_ => SseResponse(AnthropicReadyEvents));
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);
        LlmChatRequest request = AnthropicRequest() with { Temperature = 0.5 };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => ReadAllAsync(client.StreamAsync(AnthropicConnection(), request)));
        Assert.Equal(0, handler.CallCount);

        LlmConnection permitted = AnthropicConnection() with
        {
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>(StringComparer.Ordinal)
            {
                ["claude-test"] = new() { SupportsTemperature = true }
            }
        };

        await ReadAllAsync(client.StreamAsync(permitted, request));

        using JsonDocument sent = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal(0.5, sent.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task AnthropicModelDiscoveryFollowsEveryPage()
    {
        var handler = new RecordingHttpMessageHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    request.RequestUri!.Query.Contains("after_id=claude-b", StringComparison.Ordinal)
                        ? """{"data":[{"id":"claude-a","display_name":"A model"}],"has_more":false,"last_id":"claude-a"}"""
                        : """{"data":[{"id":"claude-b","display_name":"B model"}],"has_more":true,"last_id":"claude-b"}""",
                    Encoding.UTF8,
                    "application/json")
            });
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);

        IReadOnlyList<LlmModel> models = await client.ListModelsAsync(AnthropicConnection());

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(["claude-a", "claude-b"], models.Select(model => model.Id));
        Assert.Contains("after_id=claude-b", handler.RequestUri?.Query);
    }

    [Fact]
    public async Task AnthropicDefaultsMaxTokensAboveTheThinkingFloor()
    {
        var handler = new RecordingHttpMessageHandler(_ => SseResponse(AnthropicReadyEvents));
        using var http = new HttpClient(handler);
        var client = new AnthropicMessagesLlmClient(http);

        await ReadAllAsync(client.StreamAsync(AnthropicConnection(), AnthropicRequest()));

        using JsonDocument sent = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal(8192, sent.RootElement.GetProperty("max_tokens").GetInt32());
    }


    [Fact]
    public async Task OAuthAndAwsSecretsAreRedactedFromProviderErrors()
    {
        const string body = """
            {"error":{"message":"oauth-secret aws-secret session-secret"}}
            """;
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(http);
        LlmConnection connection = ResponsesConnection() with
        {
            Credential = new LlmCredential
            {
                OAuth = new LlmOAuthTokens { AccessToken = "oauth-secret" },
                Aws = new LlmAwsCredentials
                {
                    Region = "us-east-1",
                    SecretAccessKey = "aws-secret",
                    SessionToken = "session-secret"
                }
            }.Serialize()
        };

        LlmProviderException exception = await Assert.ThrowsAsync<LlmProviderException>(() =>
            ReadAllAsync(client.StreamAsync(
                connection,
                new LlmChatRequest { ModelId = "gpt-test" })));

        Assert.DoesNotContain("oauth-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("aws-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("session-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingHonorsCancellation()
    {
        var handler = new BlockingHttpMessageHandler();
        using var http = new HttpClient(handler);
        var client = new OpenAiResponsesLlmClient(http);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAllAsync(
            client.StreamAsync(
                ResponsesConnection(),
                new LlmChatRequest { ModelId = "gpt-test" },
                cancellation.Token)));
    }

    [Fact]
    public void ModelCapabilitiesOverrideConnectionCapabilities()
    {
        using var http = new HttpClient(new RecordingHttpMessageHandler(_ => SseResponse(string.Empty)));
        var client = new OpenAiCompatibleChatLlmClient(http);
        LlmConnection connection = CompatibleConnection() with
        {
            CapabilityOverrides = new LlmCapabilityOverrides { SupportsJsonObject = true },
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
            {
                ["reasoning-model"] = new LlmCapabilityOverrides
                {
                    SupportsReasoningEffort = true,
                    SupportsTemperature = false
                }
            }
        };

        LlmProviderCapabilities connectionCapabilities = client.GetCapabilities(connection);
        LlmProviderCapabilities modelCapabilities = client.GetCapabilities(connection, "reasoning-model");

        Assert.True(connectionCapabilities.SupportsJsonObject);
        Assert.False(connectionCapabilities.SupportsReasoningEffort);
        Assert.True(modelCapabilities.SupportsJsonObject);
        Assert.True(modelCapabilities.SupportsReasoningEffort);
        Assert.False(modelCapabilities.SupportsTemperature);
    }

    private static LlmConnection ResponsesConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.OpenAiResponses,
        Endpoint = "https://api.openai.test/v1",
        Credential = new LlmCredential { ApiKey = "sk-test-secret" }.Serialize()
    };

    private static LlmConnection CompatibleConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.OpenAiCompatibleChat,
        Endpoint = "https://router.test/v1",
        Credential = new LlmCredential { ApiKey = "router-secret" }.Serialize()
    };

    private const string AnthropicReadyEvents = """
        data: {"type":"message_start","message":{"id":"msg_ok","usage":{"input_tokens":3}}}

        data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"ready"}}

        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":1}}

        data: {"type":"message_stop"}

        """;

    private static LlmConnection AnthropicConnection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AnthropicMessages,
        Endpoint = "https://api.anthropic.test",
        Credential = new LlmCredential { ApiKey = "anthropic-key" }.Serialize()
    };

    private static LlmChatRequest AnthropicRequest() => new()
    {
        ModelId = "claude-test",
        Messages = [new LlmChatMessage("user", "Ready?")]
    };

    private static JsonElement Schema(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage SseResponse(string events, string? requestId = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
        };
        if (requestId is not null)
        {
            response.Headers.Add("x-request-id", requestId);
        }

        return response;
    }

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(IAsyncEnumerable<T> source)
    {
        var results = new List<T>();
        await foreach (T item in source)
        {
            results.Add(item);
        }

        return results;
    }

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private readonly Dictionary<string, IReadOnlyList<string>> _headers =
            new(StringComparer.OrdinalIgnoreCase);

        public int CallCount { get; private set; }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        public string? Body { get; private set; }

        public IReadOnlyList<string> GetHeaderValues(string name) =>
            _headers.TryGetValue(name, out IReadOnlyList<string>? values) ? values : [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                _headers[name] = [.. values];
            }

            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory(request);
        }
    }

    private sealed class BlockingHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }


    private sealed class UnexpectedClaudeRunner : IClaudeCliRunner
    {
        public Task<ClaudeCliProcessResult> RunAsync(
            ClaudeCliInvocation invocation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Claude CLI was not expected for catalog discovery.");
    }
}
