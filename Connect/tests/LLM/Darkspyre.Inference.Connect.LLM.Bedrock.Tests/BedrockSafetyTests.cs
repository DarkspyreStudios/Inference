using Darkspyre.Inference.Common;
using System.Text.Json;
using System.Globalization;
using System.Net;
using Amazon;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.BedrockAgentCore;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.BedrockRuntime.Model.Internal.MarshallTransformations;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Connect.LLM.Bedrock;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using AgentCore = Amazon.BedrockAgentCore.Model;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockSafetyTests
{
    [Theory]
    [InlineData(false, "anthropic.claude-opus-4-6-v1", LlmReasoningLevel.ExtraHigh, "high", false)]
    [InlineData(true, "anthropic.claude-opus-4-6-v1", LlmReasoningLevel.ExtraHigh, "high", false)]
    [InlineData(false, "anthropic.claude-sonnet-5-5-v1", LlmReasoningLevel.None, "low", false)]
    [InlineData(true, "anthropic.claude-unfamiliar-v1:0", LlmReasoningLevel.Maximum, "max", false)]
    [InlineData(false, "anthropic.claude-unfamiliar-v1:0", LlmReasoningLevel.Maximum, "low", true)]
    public async Task ClaudeEffortMappingReachesConverseAfterAdmission(
        bool streaming, string model, LlmReasoningLevel effort, string expected, bool narrow)
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => new StreamControl(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker
        {
            Response = StubConverseInvoker.Converse([new() { Text = "answer" }], "end_turn", null),
            Events = [new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { Text = "answer" } },
                new MessageStopEvent { StopReason = new("end_turn") }]
        };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = ResourceConnection(streaming) with
        {
            CapabilityOverrides = new()
            {
                SupportsStreaming = streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported,
                ReasoningLevels = narrow ? Darkspyre.Inference.Connect.LLM.Providers.LlmReasoningEffort.Levels(LlmReasoningLevel.Low) : null
            }
        };
        LlmChatRequest request = Request() with { ModelId = model, ReasoningLevel = effort };
        var events = new List<LlmStreamEvent>();

        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);

        var wire = streaming
            ? ConverseStreamRequestMarshaller.Instance.Marshall(Assert.Single(invoker.StreamRequests))
            : ConverseRequestMarshaller.Instance.Marshall(Assert.Single(invoker.ConverseRequests));
        using Stream body = wire.ContentStream;
        using JsonDocument sent = JsonDocument.Parse(body);
        Assert.Equal(expected, sent.RootElement.GetProperty("additionalModelRequestFields")
            .GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(effort, request.ReasoningLevel);
        Assert.Equal("answer", Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result.Text);
    }

    [Theory]
    [InlineData("amazon.nova-micro-v1:0")]
    [InlineData("amazon.nova-lite-v1:0")]
    [InlineData("amazon.nova-pro-v1:0")]
    [InlineData("amazon.nova-premier-v1:0")]
    [InlineData("us.amazon.nova-lite-v1:0")]
    [InlineData("amazon.nova-2-lite-v1:0")]
    public async Task NovaTopKReachesBothConverseTransportsWithoutAddingReasoning(string modelId)
    {
        foreach (bool streaming in new[] { false, true })
        {
            foreach (int topK in new[] { 0, 128 })
            {
                await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
                    _ => new StreamControl(), _ => throw new NotSupportedException());
                await using var guard = new BedrockLoggingGuard();
                var invoker = new StubConverseInvoker
                {
                    Response = StubConverseInvoker.Converse([new() { Text = "answer" }], "end_turn", null),
                    Events = [new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { Text = "answer" } },
                        new MessageStopEvent { StopReason = new("end_turn") }]
                };
                var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
                Assert.Equal(InferenceSupport.Supported, client.GetCapabilities(ResourceConnection(streaming), modelId).SupportsTopK);
                LlmConnection connection = ResourceConnection(streaming) with
                { CapabilityOverrides = new() { SupportsStreaming = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported), SupportsTopK = InferenceSupport.Supported } };
                if (modelId != "amazon.nova-2-lite-v1:0")
                    Assert.Empty(client.GetCapabilities(connection, modelId).ReasoningLevels);
                LlmChatRequest request = Request() with { ModelId = modelId, TopK = topK };
                await using var providers = new LlmProviderService(new Source(connection), [client]);
                var events = new List<LlmStreamEvent>();
                await foreach (LlmStreamEvent item in providers.StreamAsync("connection", request)) events.Add(item);
                var wire = streaming
                    ? ConverseStreamRequestMarshaller.Instance.Marshall(Assert.Single(invoker.StreamRequests))
                    : ConverseRequestMarshaller.Instance.Marshall(Assert.Single(invoker.ConverseRequests));
                Assert.Equal(modelId, Assert.Single(wire.PathResources).Value);
                using Stream body = wire.ContentStream;
                using JsonDocument sent = JsonDocument.Parse(body);
                JsonElement additional = sent.RootElement.GetProperty("additionalModelRequestFields");
                JsonElement config = additional.GetProperty("inferenceConfig");
                Assert.Equal(topK, config.GetProperty("topK").GetInt32());
                Assert.False(config.TryGetProperty("reasoningConfig", out _));
                Assert.False(additional.TryGetProperty("top_k", out _));
                LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
                Assert.Equal("answer", result.Text);
                Assert.Equal(LlmCompletionStatus.Completed, result.Status);
                Assert.Equal(topK, request.TopK);
            }
        }
    }

    [Theory]
    [InlineData("amazon.nova-lite-v1:0", -1)]
    [InlineData("amazon.nova-lite-v1:0", 129)]
    [InlineData("anthropic.claude-haiku-4-5-20251001-v1:0", 0)]
    public async Task TopKBoundsRefuseBeforeConverseAcquisition(string modelId, int topK)
    {
        foreach (bool streaming in new[] { false, true })
        {
            int acquired = 0;
            await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
                _ => { acquired++; throw new InvalidOperationException("SDK acquired before top-k refusal"); },
                _ => throw new NotSupportedException());
            await using var guard = new BedrockLoggingGuard();
            var invoker = new StubConverseInvoker();
            var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            {
                await foreach (LlmStreamEvent _ in client.StreamAsync(ResourceConnection(streaming),
                    Request() with { ModelId = modelId, TopK = topK })) { }
            });
            Assert.Equal(0, acquired);
            Assert.Empty(invoker.ConverseRequests);
            Assert.Empty(invoker.StreamRequests);
        }
    }

    [Theory]
    [InlineData(false, "arn:aws:bedrock:us-east-1::foundation-model/amazon.nova-micro-v1:0", false)]
    [InlineData(true, "arn:aws:bedrock:us-east-1::foundation-model/amazon.nova-micro-v1:0", false)]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(false, "model-fixture-secret", true)]
    [InlineData(true, "model-fixture-secret", true)]
    public async Task RoutedModelIdentityRemainsActualOrUnknownAndRejectsProtectedValues(bool streaming, string? observedModel, bool invalid)
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => new StreamControl(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var response = StubConverseInvoker.Converse([new() { Text = "answer" }], "end_turn", null);
        response.Trace = new() { PromptRouter = new() { InvokedModelId = observedModel } };
        var invoker = new StubConverseInvoker
        {
            Response = response,
            Events = [new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { Text = "answer" } },
                new MessageStopEvent { StopReason = new("end_turn") }, new ConverseStreamMetadataEvent
                { Trace = new() { PromptRouter = new() { InvokedModelId = observedModel } } }]
        };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        var events = new List<LlmStreamEvent>();
        async Task GenerateAsync()
        {
            await foreach (LlmStreamEvent item in client.StreamAsync(ResourceConnection(streaming), Request())) events.Add(item);
        }

        if (invalid)
        {
            LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(GenerateAsync);
            Assert.Equal("invalid_model_observation", failure.ProviderCode);
            Assert.DoesNotContain("fixture-secret", failure.ToString(), StringComparison.Ordinal);
            Assert.Empty(events.OfType<LlmChatCompletionEvent>());
        }
        else
        {
            await GenerateAsync();
            LlmChatResult result = Assert.Single(events.OfType<LlmChatCompletionEvent>()).Result;
            LlmChatResult restored = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(result,
                ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;
            Assert.Equal(observedModel, restored.ModelId);
            Assert.Equal("answer", restored.Text);
            Assert.Equal(LlmCompletionStatus.Completed, restored.Status);
        }
        Assert.Equal(streaming ? 1 : 0, invoker.StreamRequests.Count);
        Assert.Equal(streaming ? 0 : 1, invoker.ConverseRequests.Count);
    }

    [Fact]
    public async Task ManagedOpeningRefusesUnmappedConnectionOptionsBeforeCatalogAcquisition()
    {
        int acquired = 0;
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => { acquired++; throw new InvalidOperationException("SDK acquired before opening refusal"); },
            _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = ResourceConnection(streaming: true) with
        { ProviderOptions = new Dictionary<string, JsonElement> { ["unsupported"] = JsonElement.Parse("true") } };
        var source = new Source(connection);
        await using var providers = new LlmProviderService(source, [client]);
        await using var agents = new LlmAgentProviderService(source, [new ManagedBedrockAgentClient(client, providers)]);

        NotSupportedException failure = await Assert.ThrowsAsync<NotSupportedException>(() =>
            agents.StartSessionAsync("connection", new() { ModelId = "amazon.nova-micro-v1:0" }));

        Assert.Equal("Bedrock provider request options are not yet mapped to Converse fields.", failure.Message);
        Assert.Equal(0, acquired);
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmappedProviderOptionsRefuseBeforeSdkAcquisition(bool connectionOptions)
    {
        int acquired = 0;
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => { acquired++; throw new InvalidOperationException("SDK acquired before option refusal"); },
            _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        var options = new Dictionary<string, JsonElement> { ["unsupported"] = JsonElement.Parse("true") };
        LlmConnection connection = ResourceConnection(streaming: true);
        LlmChatRequest request = Request();
        if (connectionOptions) connection = connection with { ProviderOptions = options };
        else request = request with { ProviderOptions = options };

        NotSupportedException failure = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
        });

        Assert.Equal("Bedrock provider request options are not yet mapped to Converse fields.", failure.Message);
        Assert.Equal(0, acquired);
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("complete")]
    [InlineData("resources")]
    [InlineData("catalog")]
    [InlineData("capabilities")]
    [InlineData("estimate")]
    [InlineData("managed")]
    public async Task CustomHeadersRefuseBeforeSdkOrResourceEffects(string operation)
    {
        int acquired = 0;
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => { acquired++; throw new InvalidOperationException("SDK acquired before header refusal"); },
            _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = ResourceConnection(streaming: operation != "complete") with
        { AdditionalHeaders = new Dictionary<string, string> { ["X-Custom"] = "header-secret" } };
        var reference = new InferenceResourceReference("workspace", "reference");
        var access = new Resources(new("workspace", "session", 1, [reference]),
            [(reference, "reference.txt", "text/plain", "reference text"u8.ToArray())]);
        LlmChatRequest request = Request();
        if (operation == "resources") request = request with { Resources = access };
        await using var providers = new LlmProviderService(new Source(connection), [client]);
        await using var agents = new LlmAgentProviderService(new Source(connection), [new ManagedBedrockAgentClient(client, providers)]);
        var events = new List<LlmStreamEvent>();

        NotSupportedException failure = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            switch (operation)
            {
                case "catalog": await client.ListModelsAsync(connection); break;
                case "capabilities": _ = client.GetCapabilities(connection, request.ModelId); break;
                case "estimate": _ = client.EstimateInputTokens(connection, request); break;
                case "managed": await agents.StartSessionAsync("connection", new() { ModelId = request.ModelId }); break;
                default:
                    await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);
                    break;
            }
        });

        Assert.Equal("Bedrock Converse does not support custom HTTP headers. Remove AdditionalHeaders from the connection.", failure.Message);
        Assert.DoesNotContain("header-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, acquired);
        Assert.Empty(access.Reads);
        Assert.Empty(events);
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClassifiedOverflowPreservesRedactedProviderDiagnosticsAndOverflowFlag(bool streaming)
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => new StreamControl(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var sdkFailure = new Amazon.BedrockRuntime.Model.ValidationException("Input is too long: fixture-secret.", ErrorType.Sender,
            "validation-fixture-secret", "request-fixture-secret", HttpStatusCode.BadRequest);
        var invoker = new StubConverseInvoker
        {
            Failure = BedrockConverseInvoker.Unwrap(
                new BedrockRuntimeEventStreamException("Stream failed.", sdkFailure), CancellationToken.None)
        };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = ResourceConnection(streaming);
        var events = new List<LlmStreamEvent>();

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            await foreach (LlmStreamEvent item in client.StreamAsync(connection, Request())) events.Add(item);
        });

        Assert.True(failure.IsContextOverflow);
        Assert.Equal(LlmTransport.AwsBedrockConverse, failure.Transport);
        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.Equal("validation-[REDACTED]", failure.ProviderCode);
        Assert.Equal("request-[REDACTED]", failure.RequestId);
        Assert.Equal("Bedrock ValidationException: Input is too long: [REDACTED].", failure.Message);
        Assert.DoesNotContain("fixture-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(events.OfType<LlmChatCompletionEvent>());
        Assert.Equal(streaming ? 1 : 0, invoker.StreamRequests.Count);
        Assert.Equal(streaming ? 0 : 1, invoker.ConverseRequests.Count);
        Assert.Equal(streaming, invoker.Disposed);
    }

    [Fact]
    public async Task ModelStreamFailurePreservesSanitizedOriginalDiagnostics()
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => new StreamControl(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker
        {
            Failure = new ModelStreamErrorException("Stream failed.", ErrorType.Sender,
                "ModelStreamErrorException", "request-fixture-secret", HttpStatusCode.FailedDependency)
            {
                OriginalMessage = "Model rejected fixture-secret input.",
                OriginalStatusCode = 400
            }
        };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        var events = new List<LlmStreamEvent>();

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            await foreach (LlmStreamEvent item in client.StreamAsync(ResourceConnection(streaming: true), Request()))
                events.Add(item);
        });

        Assert.Contains("Stream failed.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Model rejected [REDACTED] input.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Original model status: 400", failure.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.FailedDependency, failure.StatusCode);
        Assert.Equal("ModelStreamErrorException", failure.ProviderCode);
        Assert.Equal("request-[REDACTED]", failure.RequestId);
        Assert.DoesNotContain("fixture-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(events.OfType<LlmChatCompletionEvent>());
        Assert.Single(invoker.StreamRequests);
        Assert.True(invoker.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpFailuresRetainRedactedExplanationAndObservedStatus(bool streaming)
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => new StreamControl(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker
        {
            Failure = new HttpRequestException("Connection rejected fixture-key and fixture-secret.",
                new IOException("Private SDK cause fixture-secret."), HttpStatusCode.BadGateway)
        };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        var events = new List<LlmStreamEvent>();
        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            await foreach (LlmStreamEvent item in client.StreamAsync(ResourceConnection(streaming), Request())) events.Add(item);
        });
        Assert.Contains("Connection rejected [REDACTED] and [REDACTED].", failure.Message, StringComparison.Ordinal);
        Assert.Equal(LlmTransport.AwsBedrockConverse, failure.Transport);
        Assert.Equal(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.Null(failure.ProviderCode);
        Assert.Null(failure.RequestId);
        Assert.Null(failure.RetryAfter);
        Assert.DoesNotContain("fixture-key", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Private SDK cause", failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(events.OfType<LlmChatCompletionEvent>());
        Assert.Equal(streaming ? 0 : 1, invoker.ConverseRequests.Count);
        Assert.Equal(streaming ? 1 : 0, invoker.StreamRequests.Count);
    }

    [Theory]
    [InlineData("client", false)]
    [InlineData("client", true)]
    [InlineData("event", true)]
    [InlineData("wrapped-http", true)]
    public async Task SdkClientAndEventFailuresRetainSafeDiagnostics(string kind, bool streaming)
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => new StreamControl(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        Exception original = kind switch
        {
            "client" => new AmazonClientException("SDK client failed for fixture-secret.", new IOException("Private SDK graph fixture-key.")),
            "event" => new BedrockRuntimeEventStreamException("SDK stream failed for fixture-secret.", new IOException("Private SDK graph fixture-key.")),
            _ => BedrockConverseInvoker.Unwrap(new BedrockRuntimeEventStreamException("Stream wrapper fixture-key.",
                new HttpRequestException("HTTP stream failed for fixture-secret.", new IOException("Private SDK graph fixture-key."), HttpStatusCode.ServiceUnavailable)),
                CancellationToken.None)
        };
        var invoker = new StubConverseInvoker { Failure = original };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        var events = new List<LlmStreamEvent>();
        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            await foreach (LlmStreamEvent item in client.StreamAsync(ResourceConnection(streaming), Request())) events.Add(item);
        });
        string explanation = kind switch { "client" => "SDK client", "event" => "SDK stream", _ => "HTTP stream" };
        Assert.Contains($"{explanation} failed for [REDACTED].", failure.Message, StringComparison.Ordinal);
        Assert.Equal(LlmTransport.AwsBedrockConverse, failure.Transport);
        Assert.Equal(kind == "wrapped-http" ? HttpStatusCode.ServiceUnavailable : (HttpStatusCode?)null, failure.StatusCode);
        Assert.Null(failure.ProviderCode);
        Assert.Null(failure.RequestId);
        Assert.Null(failure.RetryAfter);
        Assert.DoesNotContain("fixture-key", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Private SDK graph", failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(events.OfType<LlmChatCompletionEvent>());
        Assert.Equal(streaming ? 0 : 1, invoker.ConverseRequests.Count);
        Assert.Equal(streaming ? 1 : 0, invoker.StreamRequests.Count);
    }

    [Fact]
    public async Task LoggingVerificationRefusalRetainsSafeAwsCauseWithoutSendingGeneration()
    {
        var control = new StreamControl
        {
            LoggingFailure = new AmazonBedrockException("Permission denied for fixture-key and fixture-secret.",
                ErrorType.Sender, "denied-fixture-secret", "request-fixture-key", HttpStatusCode.Forbidden)
        };
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => control, _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(ResourceConnection(streaming: true), Request())) { }
        });
        Assert.Contains("model invocation logging could not be verified", failure.Message, StringComparison.Ordinal);
        LlmProviderException cause = Assert.IsType<LlmProviderException>(failure.InnerException);
        Assert.Contains("Permission denied for [REDACTED] and [REDACTED].", cause.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, cause.StatusCode);
        Assert.Equal("denied-[REDACTED]", cause.ProviderCode);
        Assert.Equal("request-[REDACTED]", cause.RequestId);
        Assert.DoesNotContain("fixture-key", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task StructuredOutputEnforcementMatchesTheActualConverseRequest(bool streaming, bool nativeSchema)
    {
        var control = new StreamControl();
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => control, _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var usage = new TokenUsage { InputTokens = 4, OutputTokens = 6, TotalTokens = 10 };
        const string answer = """{"word":"OK"}""";
        var invoker = new StubConverseInvoker
        {
            Response = StubConverseInvoker.Converse([new() { Text = answer }], "end_turn", usage),
            Events = [new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { Text = answer } },
                new MessageStopEvent { StopReason = new("end_turn") }, new ConverseStreamMetadataEvent { Usage = usage }]
        };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = ResourceConnection(streaming) with
        {
            CapabilityOverrides = new() { SupportsStreaming = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported), SupportsJsonSchema = (nativeSchema ? InferenceSupport.Supported : InferenceSupport.Unsupported) }
        };
        LlmChatRequest request = Request() with
        {
            ModelId = "openai.gpt-oss-20b-1:0",
            StructuredOutput = new()
            {
                Name = "answer",
                Description = "The requested word",
                Schema = JsonElement.Parse("""{ "type": "object", "properties": { "word": { "type": "string" } }, "required": ["word"], "additionalProperties": false }"""),
                Fallback = nativeSchema ? LlmStructuredOutputFallback.None : LlmStructuredOutputFallback.LocalValidation
            }
        };
        request = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(request,
            ConnectLlmJson.ChatRequest), ConnectLlmJson.ChatRequest)!;
        await using var providers = new LlmProviderService(new Source(connection), [client]);
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in providers.StreamAsync("connection", request)) events.Add(item);
        OutputConfig? config = streaming
            ? Assert.Single(invoker.StreamRequests).OutputConfig
            : Assert.Single(invoker.ConverseRequests).OutputConfig;
        using Stream body = streaming
            ? ConverseStreamRequestMarshaller.Instance.Marshall(invoker.StreamRequests[0]).ContentStream
            : ConverseRequestMarshaller.Instance.Marshall(invoker.ConverseRequests[0]).ContentStream;
        using JsonDocument sent = JsonDocument.Parse(body);
        if (nativeSchema)
        {
            Assert.NotNull(config);
            Assert.Equal(OutputFormatType.Json_schema, config.TextFormat.Type);
            JsonSchemaDefinition schema = config.TextFormat.Structure.JsonSchema;
            Assert.Equal(request.StructuredOutput!.Name, schema.Name);
            Assert.Equal(request.StructuredOutput.Description, schema.Description);
            Assert.Equal(request.StructuredOutput.Schema.GetRawText(), schema.Schema);
            JsonElement format = sent.RootElement.GetProperty("outputConfig").GetProperty("textFormat");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            JsonElement wireSchema = format.GetProperty("structure").GetProperty("jsonSchema");
            Assert.Equal(schema.Name, wireSchema.GetProperty("name").GetString());
            Assert.Equal(schema.Description, wireSchema.GetProperty("description").GetString());
            Assert.Equal(schema.Schema, wireSchema.GetProperty("schema").GetString());
        }
        else
        {
            Assert.Null(config);
            Assert.False(sent.RootElement.TryGetProperty("outputConfig", out _));
        }
        LlmChatResult terminal = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        LlmChatResult restored = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(terminal,
            ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;
        Assert.Equal(answer, restored.Text);
        Assert.Equal(LlmCompletionStatus.Completed, restored.Status);
        Assert.Equal(nativeSchema ? LlmStructuredOutputEnforcement.ProviderJsonSchema
            : LlmStructuredOutputEnforcement.LocalValidationOnly, restored.StructuredOutput!.Enforcement);
        Assert.True(restored.StructuredOutput.WasEvaluated);
        Assert.True(restored.StructuredOutput.IsValid);
        Assert.Equal(new LlmUsage(4, 6, 10), restored.Usage);
        await providers.DisposeAsync();
        await factory.DisposeAsync();
        Assert.True(control.Disposed);
        Assert.Equal(streaming, invoker.Disposed);
    }

    [Fact]
    public async Task NativeSchemaCapabilitiesUseExactModelFactsAndExplicitOverrides()
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker(), NullLogger<BedrockLlmClient>.Instance);
        foreach (string model in new[] { "openai.gpt-oss-20b-1:0", "openai.gpt-oss-120b-1:0",
                     "anthropic.claude-sonnet-4-6", "us.anthropic.claude-sonnet-4-6" })
        {
            Assert.Equal(InferenceSupport.Supported, client.GetCapabilities(Connection(), model).SupportsJsonSchema);
            Assert.Equal(InferenceSupport.Unsupported, client.GetCapabilities(Connection() with
            { CapabilityOverrides = new() { SupportsJsonSchema = InferenceSupport.Unsupported } }, model).SupportsJsonSchema);
        }
        foreach (string model in new[] { "openai.gpt-oss-20b-2:0", "anthropic.claude-haiku",
                     "arn:aws:bedrock:us-east-1:123456789012:inference-profile/unknown" })
        {
            Assert.Equal(InferenceSupport.Unknown, client.GetCapabilities(Connection(), model).SupportsJsonSchema);
            Assert.Equal(InferenceSupport.Supported, client.GetCapabilities(Connection() with
            { CapabilityOverrides = new() { SupportsJsonSchema = InferenceSupport.Supported } }, model).SupportsJsonSchema);
        }
        Assert.Equal(InferenceSupport.Unknown, client.GetCapabilities(Connection() with
        {
            CapabilityOverrides = new()
            {
                SupportsJsonSchema = InferenceSupport.Unknown,
                SupportsJsonObject = InferenceSupport.Unknown,
                SupportsToolUse = InferenceSupport.Unknown,
                SupportsPromptImages = InferenceSupport.Unknown,
                SupportsPromptDocuments = InferenceSupport.Unknown
            }
        }, "deepseek.r1-v1:0").SupportsJsonSchema);
    }

    [Theory]
    [InlineData("jp.", "anthropic.claude-sonnet-4-6", true)]
    [InlineData("au.", "anthropic.claude-sonnet-4-6", true)]
    [InlineData("in.", "anthropic.claude-haiku-4-5-20251001-v1:0", false)]
    public async Task GeographicProfilesRetainModelFactsAndNativeRequestFields(string prefix, string baseModel, bool nativeSchema)
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker(), NullLogger<BedrockLlmClient>.Instance);
        string modelId = prefix + baseModel;
        LlmConnection connection = Connection();
        LlmProviderCapabilities capabilities = client.GetCapabilities(connection, modelId);
        Assert.Contains(LlmReasoningLevel.Low, capabilities.ReasoningLevels);
        Assert.True(BedrockModelResolver.LooksLikeInferenceProfile(modelId));
        Assert.Equal(BedrockModelLimits.Get(baseModel), BedrockModelLimits.Get(modelId));
        Assert.Equal(nativeSchema ? InferenceSupport.Supported : InferenceSupport.Unsupported, capabilities.SupportsJsonSchema);
        Assert.Equal(InferenceSupport.Supported, capabilities.PromptCache.AcceptsHints);
        LlmChatRequest request = Request() with
        {
            ModelId = modelId,
            ReasoningLevel = LlmReasoningLevel.Low,
            PromptCache = new() { Scope = LlmPromptCacheScope.Conversation },
            StructuredOutput = new()
            {
                Name = "answer",
                Schema = JsonElement.Parse("""{"type":"object"}"""),
                Fallback = LlmStructuredOutputFallback.LocalValidation
            }
        };
        _ = client.ValidateRequest(connection, request);
        var enforcement = StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);
        Assert.Equal(nativeSchema ? LlmStructuredOutputEnforcement.ProviderJsonSchema
            : LlmStructuredOutputEnforcement.LocalValidationOnly, enforcement);
        ConverseRequest built = BedrockLlmClient.BuildRequest(connection, request, modelId, enforcement);
        ConverseRequest expected = BedrockLlmClient.BuildRequest(connection, request with { ModelId = baseModel }, baseModel);
        foreach (var wire in new[]
        {
            ConverseRequestMarshaller.Instance.Marshall(built),
            ConverseStreamRequestMarshaller.Instance.Marshall(BedrockLlmClient.ToStreamRequest(built))
        })
        {
            Assert.Equal(modelId, Assert.Single(wire.PathResources).Value);
            using Stream body = wire.ContentStream;
            using JsonDocument sent = JsonDocument.Parse(body);
            Assert.Equal(BedrockDocumentJson.Write(expected.AdditionalModelRequestFields),
                sent.RootElement.GetProperty("additionalModelRequestFields").GetRawText());
            Assert.Equal(nativeSchema, sent.RootElement.TryGetProperty("outputConfig", out _));
            Assert.Equal("default", sent.RootElement.GetProperty("messages")[0].GetProperty("content")[1]
                .GetProperty("cachePoint").GetProperty("type").GetString());
        }
    }

    [Theory]
    [InlineData("openai.gpt-oss-20b-1:0")]
    [InlineData("openai.gpt-oss-120b-1:0")]
    public async Task GovCloudProfilesRetainGptOssFactsAndNativeRequestIdentity(string baseModel)
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker(), NullLogger<BedrockLlmClient>.Instance);
        string modelId = "us-gov." + baseModel;
        LlmConnection connection = Connection();
        LlmProviderCapabilities capabilities = client.GetCapabilities(connection, modelId);
        Assert.Contains(LlmReasoningLevel.Low, capabilities.ReasoningLevels);
        Assert.Equal(InferenceSupport.Supported, capabilities.SupportsJsonSchema);
        Assert.Equal(InferenceSupport.Unsupported, capabilities.SupportsPromptImages);
        Assert.Equal(InferenceSupport.Unsupported, capabilities.SupportsPromptDocuments);
        Assert.True(BedrockModelResolver.LooksLikeInferenceProfile(modelId));
        Assert.Equal(BedrockModelFamily.OpenAiGptOss, BedrockModelFamilies.Classify(modelId).Family);
        Assert.Equal(baseModel, BedrockModelFamilies.Classify(modelId).BaseModelId);
        LlmChatRequest request = Request() with
        {
            ModelId = modelId,
            ReasoningLevel = LlmReasoningLevel.Low,
            StructuredOutput = new() { Name = "answer", Schema = JsonElement.Parse("""{"type":"object"}""") }
        };
        _ = client.ValidateRequest(connection, request);
        var enforcement = StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, enforcement);
        ConverseRequest built = BedrockLlmClient.BuildRequest(connection, request, modelId, enforcement);
        foreach (var wire in new[]
        {
            ConverseRequestMarshaller.Instance.Marshall(built),
            ConverseStreamRequestMarshaller.Instance.Marshall(BedrockLlmClient.ToStreamRequest(built))
        })
        {
            Assert.Equal(modelId, Assert.Single(wire.PathResources).Value);
            using Stream body = wire.ContentStream;
            using JsonDocument sent = JsonDocument.Parse(body);
            Assert.Equal("low", sent.RootElement.GetProperty("additionalModelRequestFields").GetProperty("reasoning_effort").GetString());
            Assert.Equal("json_schema", sent.RootElement.GetProperty("outputConfig").GetProperty("textFormat").GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task IndiaHaikuCannotEnableUnsupportedNativeSchemas()
    {
        int acquired = 0;
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => { acquired++; throw new InvalidOperationException("SDK acquired before schema refusal"); },
            _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = ResourceConnection(streaming: true) with
        { CapabilityOverrides = new() { SupportsJsonSchema = InferenceSupport.Supported } };
        LlmChatRequest request = Request() with
        {
            ModelId = "in.anthropic.claude-haiku-4-5-20251001-v1:0",
            StructuredOutput = new() { Name = "answer", Schema = JsonElement.Parse("""{"type":"object"}""") }
        };
        NotSupportedException failure = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
        });
        Assert.Equal("Bedrock capability overrides cannot enable JSON Schema output for the India Haiku 4.5 inference profile.", failure.Message);
        Assert.Equal(0, acquired);
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Fact]
    public async Task IndiaHaikuRefusesStrictToolsBeforeSdkAcquisition()
    {
        int acquired = 0;
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => { acquired++; throw new InvalidOperationException("SDK acquired before strict tool refusal"); },
            _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        var tool = new LlmToolDefinition
        {
            Name = "notes",
            Strict = true,
            InputSchema = JsonElement.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""")
        };
        LlmChatRequest request = Request() with
        { ModelId = "in.anthropic.claude-haiku-4-5-20251001-v1:0", Tools = [tool] };
        NotSupportedException failure = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(ResourceConnection(streaming: true), request)) { }
        });
        Assert.Equal("The selected route cannot enforce strict tool input schemas.", failure.Message);
        Assert.Equal(LlmStrictInputSchemaSupport.Unsupported,
            client.GetCapabilities(Connection(), request.ModelId).Tools.StrictInputSchema);
        Assert.True(tool.Strict);
        Assert.Same(tool, Assert.Single(request.Tools));
        Assert.Equal(0, acquired);
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Fact]
    public async Task IndiaHaikuOrdinaryToolsRetainOriginalSchemasWithoutStrictEnforcement()
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker(), NullLogger<BedrockLlmClient>.Instance);
        JsonElement schema = JsonElement.Parse("""{"type":"object","properties":{"text":{"type":"string","maxLength":100}},"required":["text"]}""");
        LlmChatRequest request = Request() with
        {
            ModelId = "in.anthropic.claude-haiku-4-5-20251001-v1:0",
            Tools = [new LlmToolDefinition { Name = "notes", InputSchema = schema }]
        };
        _ = client.ValidateRequest(Connection(), request);
        ConverseRequest built = BedrockLlmClient.BuildRequest(Connection(), request, request.ModelId);
        foreach (var wire in new[]
        {
            ConverseRequestMarshaller.Instance.Marshall(built),
            ConverseStreamRequestMarshaller.Instance.Marshall(BedrockLlmClient.ToStreamRequest(built))
        })
        {
            Assert.Equal(request.ModelId, Assert.Single(wire.PathResources).Value);
            using Stream body = wire.ContentStream;
            using JsonDocument sent = JsonDocument.Parse(body);
            JsonElement specification = sent.RootElement.GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec");
            Assert.Equal("notes", specification.GetProperty("name").GetString());
            Assert.False(specification.TryGetProperty("strict", out _));
            Assert.Equal(schema.GetRawText(), specification.GetProperty("inputSchema").GetProperty("json").GetRawText());
        }
    }

    [Theory]
    [InlineData("us.amazon.nova-micro-v1:0")]
    [InlineData("eu.amazon.nova-lite-v1:0")]
    [InlineData("amazon.nova-pro-v1:0")]
    [InlineData("jp.amazon.nova-2-lite-v1:0")]
    public async Task DocumentedNovaModelsRefuseStrictToolsBeforeSdkAcquisition(string modelId)
    {
        int acquired = 0;
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => { acquired++; throw new InvalidOperationException("SDK acquired before strict tool refusal"); },
            _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmChatRequest request = Request() with
        {
            ModelId = modelId,
            Tools = [new LlmToolDefinition { Name = "notes", Strict = true, InputSchema = JsonElement.Parse("""{"type":"object"}""") }]
        };
        NotSupportedException failure = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(ResourceConnection(streaming: true), request)) { }
        });
        Assert.Equal("The selected route cannot enforce strict tool input schemas.", failure.Message);
        Assert.Equal(LlmStrictInputSchemaSupport.Unsupported, client.GetCapabilities(Connection(), modelId).Tools.StrictInputSchema);
        Assert.True(Assert.IsType<LlmToolDefinition>(Assert.Single(request.Tools)).Strict);
        Assert.Equal(0, acquired);
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Fact]
    public async Task DocumentedNovaCannotEnableUnsupportedNativeSchemas()
    {
        int acquired = 0;
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => { acquired++; throw new InvalidOperationException("SDK acquired before schema refusal"); },
            _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmChatRequest request = Request() with
        {
            ModelId = "us.amazon.nova-micro-v1:0",
            StructuredOutput = new() { Name = "answer", Schema = JsonElement.Parse("""{"type":"object"}""") }
        };
        LlmConnection connection = ResourceConnection(streaming: true) with
        { CapabilityOverrides = new() { SupportsJsonSchema = InferenceSupport.Supported } };
        NotSupportedException failure = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
        });
        Assert.Equal("Bedrock capability overrides cannot enable JSON Schema output for this Nova model.", failure.Message);
        Assert.Equal(0, acquired);
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Theory]
    [InlineData("us.amazon.nova-micro-v1:0")]
    [InlineData("eu.amazon.nova-lite-v1:0")]
    [InlineData("amazon.nova-pro-v1:0")]
    [InlineData("jp.amazon.nova-2-lite-v1:0")]
    public async Task DocumentedNovaOrdinaryToolsRetainOriginalSchemas(string modelId)
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker(), NullLogger<BedrockLlmClient>.Instance);
        JsonElement schema = JsonElement.Parse("""{"type":"object","properties":{"text":{"type":"string","maxLength":100}},"required":["text"]}""");
        LlmChatRequest request = Request() with
        { ModelId = modelId, Tools = [new LlmToolDefinition { Name = "notes", InputSchema = schema }] };
        _ = client.ValidateRequest(Connection(), request);
        ConverseRequest built = BedrockLlmClient.BuildRequest(Connection(), request, modelId);
        foreach (var wire in new[]
        {
            ConverseRequestMarshaller.Instance.Marshall(built),
            ConverseStreamRequestMarshaller.Instance.Marshall(BedrockLlmClient.ToStreamRequest(built))
        })
        {
            Assert.Equal(modelId, Assert.Single(wire.PathResources).Value);
            using Stream body = wire.ContentStream;
            using JsonDocument sent = JsonDocument.Parse(body);
            JsonElement specification = sent.RootElement.GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec");
            Assert.False(specification.TryGetProperty("strict", out _));
            Assert.Equal(schema.GetRawText(), specification.GetProperty("inputSchema").GetProperty("json").GetRawText());
        }
    }

    [Theory]
    [InlineData("us.anthropic.claude-sonnet-4-5-20250929-v1:0")]
    [InlineData("eu.anthropic.claude-haiku-4-5-20251001-v1:0")]
    [InlineData("global.anthropic.claude-opus-4-5-20251101-v1:0")]
    [InlineData("anthropic.claude-opus-4-6-v1")]
    public async Task DocumentedClaudeModelsAdmitNativeSchemasWithoutOverrides(string modelId)
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker(), NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = Connection();
        LlmChatRequest request = Request() with
        {
            ModelId = modelId,
            StructuredOutput = new()
            {
                Name = "answer",
                Schema = JsonElement.Parse("""{"type":"object","properties":{"word":{"type":"string"}},"required":["word"],"additionalProperties":false}""")
            }
        };

        LlmProviderCapabilities capabilities = client.ValidateRequest(connection, request);
        Assert.Equal(InferenceSupport.Supported, capabilities.SupportsJsonSchema);
        LlmStructuredOutputEnforcement enforcement = StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);
        Assert.Equal(LlmStructuredOutputEnforcement.ProviderJsonSchema, enforcement);
        ConverseRequest built = BedrockLlmClient.BuildRequest(connection, request, modelId, enforcement);
        foreach (Stream body in new[]
        {
            ConverseRequestMarshaller.Instance.Marshall(built).ContentStream,
            ConverseStreamRequestMarshaller.Instance.Marshall(BedrockLlmClient.ToStreamRequest(built)).ContentStream
        })
        {
            using (body)
            using (JsonDocument sent = JsonDocument.Parse(body))
            {
                JsonElement format = sent.RootElement.GetProperty("outputConfig").GetProperty("textFormat");
                Assert.Equal("json_schema", format.GetProperty("type").GetString());
                Assert.Equal(request.StructuredOutput.Schema.GetRawText(),
                    format.GetProperty("structure").GetProperty("jsonSchema").GetProperty("schema").GetString());
            }
        }
    }

    [Theory]
    [InlineData("", "{}", false)]
    [InlineData("answer", "[]", false)]
    [InlineData("answer", "{}", true)]
    public async Task InvalidStructuredControlsRefuseBeforeSdkAcquisition(string name, string schema, bool jsonObject)
    {
        int acquired = 0;
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => { acquired++; throw new InvalidOperationException("SDK acquired before refusal"); },
            _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = ResourceConnection(streaming: true) with
        { CapabilityOverrides = new() { SupportsJsonSchema = InferenceSupport.Supported, SupportsJsonObject = (jsonObject ? InferenceSupport.Supported : InferenceSupport.Unsupported) } };
        LlmChatRequest request = Request() with
        { StructuredOutput = new() { Name = name, Schema = JsonElement.Parse(schema) } };
        Exception error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, request)) { }
        });
        if (jsonObject) Assert.IsType<NotSupportedException>(error);
        else Assert.IsType<ArgumentException>(error);
        Assert.Equal(0, acquired);
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Theory]
    [InlineData("amazon.nova-micro-v1:0", LlmPromptCacheRetention.Default, null)]
    [InlineData("us.amazon.nova-micro-v1:0", LlmPromptCacheRetention.Default, null)]
    [InlineData("amazon.nova-micro-v1:0", LlmPromptCacheRetention.Extended, null)]
    [InlineData("anthropic.claude-sonnet-4-6", LlmPromptCacheRetention.Default, null)]
    [InlineData("anthropic.claude-sonnet-4-6", LlmPromptCacheRetention.Extended, "1h")]
    public void PromptCacheDefaultOmitsTheExtendedTtlFieldInBothConverseModes(
        string modelId, LlmPromptCacheRetention retention, string? expectedTtl)
    {
        ConverseRequest request = BedrockLlmClient.BuildRequest(Connection(), Request(instructions: "Reply concisely") with
        {
            ModelId = modelId,
            PromptCache = new() { Scope = LlmPromptCacheScope.Conversation, Retention = retention }
        }, modelId);
        ConverseStreamRequest stream = BedrockLlmClient.ToStreamRequest(request);
        CachePointBlock[] points = [request.System[^1].CachePoint, request.Messages[^1].Content[^1].CachePoint,
            stream.System[^1].CachePoint, stream.Messages[^1].Content[^1].CachePoint];
        Assert.All(points, point =>
        {
            Assert.Equal(CachePointType.Default, point.Type);
            Assert.Equal(expectedTtl, point.Ttl?.Value);
        });
    }

    [Theory]
    [InlineData("amazon.nova-micro-v1:0", 0)]
    [InlineData("us.amazon.nova-micro-v1:0", 0)]
    [InlineData("amazon.nova-lite-v1:0", 0)]
    [InlineData("amazon.nova-pro-v1:0", 0)]
    [InlineData("amazon.nova-2-lite-v1:0", 0)]
    [InlineData("anthropic.claude-sonnet-4-6", 2)]
    public void ConversationCachePreservesToolMessagesAndUsesAnEligibleCheckpointInBothConverseModes(
        string modelId, int checkpointMessage)
    {
        ConverseRequest request = BedrockLlmClient.BuildRequest(Connection(), Request(messages:
        [
            new("user", "Echo this text"),
            new("assistant", "Calling echo") { ToolCalls = [new LlmToolCall("echo-1", "echo", "{}")] },
            new("user", string.Empty)
            {
                ToolResults = [new LlmToolResult("echo-1", new() { Parts = [new LlmToolOutputText("echoed text")] })]
            }
        ]) with
        {
            ModelId = modelId,
            PromptCache = new() { Scope = LlmPromptCacheScope.Conversation },
            Tools = [new LlmToolDefinition { Name = "echo", InputSchema = JsonElement.Parse("""{"type":"object"}""") }]
        }, modelId);
        ConverseStreamRequest stream = BedrockLlmClient.ToStreamRequest(request);
        foreach (List<Message> messages in new[] { request.Messages, stream.Messages })
        {
            Assert.Equal(3, messages.Count);
            Assert.Equal("Echo this text", messages[0].Content[0].Text);
            Assert.Equal("Calling echo", messages[1].Content[0].Text);
            Assert.Equal("echo-1", messages[1].Content[1].ToolUse.ToolUseId);
            ToolResultBlock result = messages[2].Content[0].ToolResult;
            Assert.Equal("echo-1", result.ToolUseId);
            Assert.Equal("echoed text", Assert.Single(result.Content).Text);
            Assert.Equal(ToolResultStatus.Success, result.Status);
            Assert.Equal(CachePointType.Default, messages[checkpointMessage].Content[^1].CachePoint?.Type);
            Assert.Single(messages.SelectMany(message => message.Content), block => block.CachePoint is not null);
        }
        Assert.Equal("echo", Assert.Single(request.ToolConfig.Tools, tool => tool.ToolSpec is not null).ToolSpec.Name);
    }

    [Theory]
    [InlineData("openai.gpt-oss-20b-1:0", LlmReasoningLevel.Low, "low")]
    [InlineData("openai.gpt-oss-20b-1:0", LlmReasoningLevel.Medium, "medium")]
    [InlineData("openai.gpt-oss-20b-1:0", LlmReasoningLevel.High, "high")]
    [InlineData("openai.gpt-oss-120b-1:0", LlmReasoningLevel.Low, "low")]
    [InlineData("openai.gpt-oss-120b-1:0", LlmReasoningLevel.Medium, "medium")]
    [InlineData("us.openai.gpt-oss-120b-1:0", LlmReasoningLevel.High, "high")]
    public async Task GptOssReasoningLevelsReachBothConverseRequestModes(
        string modelId, LlmReasoningLevel level, string expectedEffort)
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker(), NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = Connection();
        Assert.Equal([LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High],
            client.GetCapabilities(connection, modelId).ReasoningLevels.Order());
        ConverseRequest request = BedrockLlmClient.BuildRequest(connection,
            Request(maxTokens: 128) with { ModelId = modelId, ReasoningLevel = level }, modelId);
        Assert.Equal(expectedEffort, Assert.Single(request.AdditionalModelRequestFields.AsDictionary()).Value.AsString());
        Assert.True(request.AdditionalModelRequestFields.AsDictionary().ContainsKey("reasoning_effort"));
        Assert.Equal(128, request.InferenceConfig.MaxTokens);
        ConverseStreamRequest stream = BedrockLlmClient.ToStreamRequest(request);
        Assert.Equal(expectedEffort, stream.AdditionalModelRequestFields.AsDictionary()["reasoning_effort"].AsString());
    }

    [Theory]
    [InlineData(LlmReasoningLevel.None, null)]
    [InlineData(LlmReasoningLevel.Low, "low")]
    [InlineData(LlmReasoningLevel.Medium, "medium")]
    [InlineData(LlmReasoningLevel.High, "high")]
    public async Task NovaLite15ReasoningUsesItsNativeFieldsWithoutNova2ControlRestrictions(
        LlmReasoningLevel level, string? effort)
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker(), NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = Connection();
        foreach (string modelId in new[] { "amazon.nova-lite-1-5-v1:0", "us.amazon.nova-lite-1-5-v1:0" })
        {
            Assert.Equal([LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High],
                client.GetCapabilities(connection, modelId).ReasoningLevels.Order());
            LlmChatRequest request = Request(maxTokens: 40000, temperature: 0) with
            { ModelId = modelId, ReasoningLevel = level, TopK = 0 };
            _ = client.ValidateRequest(connection, request);
            ConverseRequest built = BedrockLlmClient.BuildRequest(connection, request, modelId);
            foreach (var wire in new[]
            {
                ConverseRequestMarshaller.Instance.Marshall(built),
                ConverseStreamRequestMarshaller.Instance.Marshall(BedrockLlmClient.ToStreamRequest(built))
            })
            {
                Assert.Equal(modelId, Assert.Single(wire.PathResources).Value);
                using Stream body = wire.ContentStream;
                using JsonDocument sent = JsonDocument.Parse(body);
                JsonElement additional = sent.RootElement.GetProperty("additionalModelRequestFields");
                JsonElement reasoning = additional.GetProperty("reasoningConfig");
                Assert.Equal(effort is null ? "disabled" : "enabled", reasoning.GetProperty("type").GetString());
                if (effort is null) Assert.False(reasoning.TryGetProperty("maxReasoningEffort", out _));
                else Assert.Equal(effort, reasoning.GetProperty("maxReasoningEffort").GetString());
                Assert.Equal(0, additional.GetProperty("inferenceConfig").GetProperty("topK").GetInt32());
                Assert.Equal(40000, sent.RootElement.GetProperty("inferenceConfig").GetProperty("maxTokens").GetInt32());
                Assert.Equal(0, sent.RootElement.GetProperty("inferenceConfig").GetProperty("temperature").GetDouble());
            }
        }
        Assert.Empty(client.GetCapabilities(connection, "amazon.nova-lite-v1:0").ReasoningLevels);
        Assert.Empty(client.GetCapabilities(connection, "amazon.nova-pro-v1:0").ReasoningLevels);
    }

    [Theory]
    [InlineData("openai.gpt-oss-safeguard-20b")]
    [InlineData("openai.gpt-oss-20b-2:0")]
    public void UnqualifiedGptOssIdsDoNotAcquireReasoningLevelsFromTheirPrefix(string modelId) =>
        Assert.Empty(BedrockLlmClient.ReasoningLevels(modelId));

    [Theory]
    [InlineData("amazon.nova-micro-v1:0")]
    [InlineData("us.amazon.nova-micro-v1:0")]
    [InlineData("openai.gpt-oss-20b-1:0")]
    [InlineData("openai.gpt-oss-120b-1:0")]
    public Task TextOnlyModelsConvertToolDocumentsAndRefusePromptDocumentsBeforeEffects(string modelId) =>
        AssertTextOnlyMediaAsync(modelId);

    [Theory]
    [InlineData("deepseek.r1-v1:0", false)]
    [InlineData("deepseek.r1-v1:0", true)]
    [InlineData("us.deepseek.r1-v1:0", false)]
    [InlineData("us.deepseek.r1-v1:0", true)]
    public async Task DeepSeekR1RefusesNativeToolsAndHistoryBeforeSdkAcquisition(string modelId, bool history)
    {
        int acquired = 0;
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => { acquired++; throw new InvalidOperationException("SDK acquired before tool refusal"); },
            _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmChatRequest request = history
            ? Request(messages:
            [
                new("user", "Read the reference"),
                new("assistant", string.Empty) { ToolCalls = [new LlmToolCall("read-1", "read", "{}")] },
                new("user", string.Empty) { ToolResults = [new LlmToolResult("read-1", new() { Parts = [new LlmToolOutputText("reference")] })] }
            ])
            with
            { ModelId = modelId }
            : Request() with
            {
                ModelId = modelId,
                Tools = [new LlmToolDefinition { Name = "read", InputSchema = JsonElement.Parse("""{"type":"object"}""") }]
            };
        NotSupportedException failure = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(ResourceConnection(streaming: true), request)) { }
        });
        Assert.Equal("The selected connection does not support host-executed tools.", failure.Message);
        LlmProviderCapabilities capabilities = client.GetCapabilities(Connection(), modelId);
        Assert.Equal(InferenceSupport.Unsupported, capabilities.SupportsToolUse);
        Assert.Equal(InferenceSupport.Unsupported, capabilities.SupportsAgentToolUse);
        Assert.Empty(capabilities.Tools.ChoiceModes);
        Assert.Equal(InferenceSupport.Supported, capabilities.SupportsAgentSessions);
        _ = client.ValidateRequest(Connection(), Request() with { ModelId = modelId });
        Assert.Equal(0, acquired);
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeepSeekR1CannotEnableNativeToolsThroughOverrides(bool perModel)
    {
        int acquired = 0;
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => { acquired++; throw new InvalidOperationException("SDK acquired before override refusal"); },
            _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        const string modelId = "us.deepseek.r1-v1:0";
        var overrides = new LlmCapabilityOverrides { SupportsToolUse = InferenceSupport.Supported };
        LlmConnection connection = perModel
            ? ResourceConnection(streaming: true) with { ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides> { [modelId] = overrides } }
            : ResourceConnection(streaming: true) with { CapabilityOverrides = overrides };
        NotSupportedException failure = await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (LlmStreamEvent _ in client.StreamAsync(connection, Request() with { ModelId = modelId })) { }
        });
        Assert.Equal("Bedrock capability overrides cannot enable native tool use for DeepSeek-R1.", failure.Message);
        Assert.Equal(0, acquired);
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Theory]
    [InlineData("deepseek.r1-v1:0")]
    [InlineData("us.deepseek.r1-v1:0")]
    public Task DeepSeekR1ConvertsToolDocumentsAndRefusesPromptMediaBeforeEffects(string modelId) =>
        AssertTextOnlyMediaAsync(modelId);

    private static async Task AssertTextOnlyMediaAsync(string modelId)
    {
        await using var factory = new BedrockClientFactory(_ => throw new InvalidOperationException("Unexpected SDK acquisition"),
            _ => throw new InvalidOperationException("Unexpected SDK acquisition"),
            _ => throw new InvalidOperationException("Unexpected SDK acquisition"));
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = Connection();
        LlmProviderCapabilities capabilities = client.GetCapabilities(connection, modelId);
        Assert.Equal(InferenceSupport.Unsupported, capabilities.SupportsPromptImages);
        Assert.Equal(InferenceSupport.Unsupported, capabilities.SupportsPromptDocuments);
        Assert.Equal(LlmRichContentCarriage.Unsupported, capabilities.RichContentSupported.Images);
        Assert.Equal(LlmRichContentCarriage.Unsupported, capabilities.RichContentSupported.PdfDocuments);
        Assert.Equal(LlmRichContentCarriage.Text, capabilities.RichContentSupported.TextDocuments);
        Assert.Equal(capabilities.RichContentSupported, capabilities.Agent.RichContentSupported);

        var attachment = new LlmAttachment
        {
            Kind = LlmBinaryKind.Document,
            MediaType = "text/plain",
            FileName = "reference.txt",
            Data = "reference text"u8.ToArray()
        };
        await using var events = client.StreamAsync(connection, Request(messages:
            [new("user", "Read the reference") { Attachments = [attachment] }]) with
        { ModelId = modelId }).GetAsyncEnumerator();
        await Assert.ThrowsAsync<NotSupportedException>(() => events.MoveNextAsync().AsTask());
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
        Assert.Throws<NotSupportedException>(() => client.GetCapabilities(connection with
        { CapabilityOverrides = new() { SupportsPromptDocuments = InferenceSupport.Supported } }, modelId));

        ConverseRequest request = BedrockLlmClient.BuildRequest(connection, Request(messages:
        [
            new("user", "Read the reference"),
            new("assistant", string.Empty) { ToolCalls = [new LlmToolCall("read-1", "read", "{}")] },
            new("user", string.Empty)
            {
                ToolResults = [new LlmToolResult("read-1", new()
                {
                    Parts = [new LlmToolOutputBinary(LlmBinaryKind.Document, "text/plain", attachment.Data, attachment.FileName)]
                })]
            }
        ]) with
        { ModelId = modelId }, modelId);
        foreach (List<Message> messages in new[] { request.Messages, BedrockLlmClient.ToStreamRequest(request).Messages })
        {
            ToolResultContentBlock content = Assert.Single(messages[^1].Content[0].ToolResult.Content);
            Assert.Null(content.Document);
            Assert.Equal("[File: reference.txt]\nreference text", content.Text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PortableDocumentsReachConverseThroughManagedSessionOpening(bool generatedByTool)
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var generated = new InferenceResourceReference("workspace", "generated-json");
        var access = new Resources(new("workspace", "session", 4, [generated, reference]),
            [(reference, "reference.txt", "text/plain", "reference text"u8.ToArray()),
                (generated, "result.json", "application/json", "{\"answer\":42}"u8.ToArray())]);
        var context = new LlmAgentContext
        {
            Revision = 9,
            ResourceSelectionVersion = new("workspace", "session", 4),
            SelectedResources = [generated, reference],
            Entries = [new("prompt", "turn-1", new("user", "Read the reference")
                { Attachments = [new LlmResourceAttachment(reference), new LlmResourceAttachment(reference)] }),
                new("reply", "turn-1", new("assistant", "Created result.json"))]
        };
        if (generatedByTool)
            context = context with
            {
                Entries = [context.Entries[0], context.Entries[1] with
                { Message = context.Entries[1].Message with { ToolCalls = [new LlmToolCall("create-1", "create_json", "{}")] } },
                    new("tool", "turn-1", new("user", string.Empty)
                    { ToolResults = [new LlmToolResult("create-1", new() { Parts = [new LlmToolOutputSessionResource(generated)] })] })]
            };
        context = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(context, ConnectLlmJson.AgentContext),
            ConnectLlmJson.AgentContext)!;
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => new StreamControl(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker
        {
            Response = StubConverseInvoker.Converse([new() { Text = "answer" }], "end_turn"),
            Events = [new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { Text = "answer" } },
                new MessageStopEvent { StopReason = new("end_turn") }]
        };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = ResourceConnection(generatedByTool);
        var source = new Source(connection);
        await using var providers = new LlmProviderService(source, [client]);
        await using var agents = new LlmAgentProviderService(source, [new ManagedBedrockAgentClient(client, providers)]);
        await using ILlmAgentSession session = await agents.StartSessionAsync("connection", new()
        {
            ModelId = Request().ModelId,
            SessionId = "session",
            Context = context,
            UnboundContinuationState = LlmUnboundContinuationState.Omit,
            CallerServices = new() { Resources = access }
        });
        LlmAgentContext imported = await session.CaptureContextAsync();
        var events = new List<LlmAgentEvent>();
        await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("Compare the files")] }))
            events.Add(item);
        Assert.Equal(LlmAgentCompletionReason.Completed, Assert.Single(events.OfType<LlmAgentTurnCompletedEvent>()).Reason);
        List<Message> messages;
        if (generatedByTool)
        {
            messages = Assert.Single(invoker.StreamRequests).Messages;
            Assert.Empty(invoker.ConverseRequests);
            Assert.True(invoker.Disposed);
        }
        else
        {
            messages = Assert.Single(invoker.ConverseRequests).Messages;
            Assert.Empty(invoker.StreamRequests);
        }
        DocumentBlock[] documents = messages.SelectMany(message => message.Content)
            .SelectMany(block => block.Document is { } document ? new[] { document }
                : block.ToolResult?.Content.Where(part => part.Document is not null).Select(part => part.Document) ?? []).ToArray();
        Assert.Equal(["reference text", "reference text", "{\"answer\":42}"],
            documents.Select(document => System.Text.Encoding.UTF8.GetString(document.Source.Bytes.ToArray())));
        Assert.All(documents, document => Assert.Equal(DocumentFormat.Txt, document.Format));
        Assert.Equal(2, access.Reads.Count);
        Assert.All(access.Reads, stream => Assert.False(stream.CanRead));
        Assert.Equal(9, imported.Revision);
        Assert.Equal(context.ResourceSelectionVersion, imported.ResourceSelectionVersion);
        Assert.Equal([generated, reference], imported.SelectedResources);
        LlmAgentContext captured = await session.CaptureContextAsync();
        Assert.Equal(context.ResourceSelectionVersion, captured.ResourceSelectionVersion);
        Assert.Equal([generated, reference], captured.SelectedResources);
        Assert.IsType<LlmResourceAttachment>(captured.Entries[0].Message.Attachments[0]);
        if (generatedByTool)
            Assert.IsType<LlmToolOutputSessionResource>(Assert.IsType<LlmToolResult>(Assert.Single(captured.Entries[2].Message.ToolResults)).Output.Parts[0]);
    }

    [Fact]
    public async Task ResourceBoundAccountingDoesNotIgnoreSelectedFilesOrReadThem()
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var access = new Resources(new("workspace", "session", 1, [reference]),
            [(reference, "reference.txt", "text/plain", "reference text"u8.ToArray())]);
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker();
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = ResourceConnection(streaming: true);
        LlmChatRequest request = Request() with { Resources = access };
        Assert.Null(client.EstimateInputTokens(connection, request));
        Assert.Null(client.EstimateInputTokens(connection, request with
        { Messages = [new("user", "Read the file") { Attachments = [new LlmResourceAttachment(reference)] }] }));
        await using var providers = new LlmProviderService(new Source(connection), [client]);
        await Assert.ThrowsAsync<NotSupportedException>(() => providers.CountInputTokensAsync("connection", request));
        Assert.Empty(access.Reads);
        Assert.Empty(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceReaderCleanupRefusalPreventsSuccessAndRemainsVisibleAtRetirement(bool generationFails)
    {
        var reference = new InferenceResourceReference("workspace", "reference");
        var cleanup = new IOException("The reader could not be released.");
        var operation = new IOException("The provider request failed.");
        var access = new Resources(new("workspace", "session", 1, [reference]),
            [(reference, "reference.txt", "text/plain", "reference text"u8.ToArray())], cleanup);
        var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => new StreamControl(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker
        {
            Failure = generationFails ? operation : null,
            Events = [new MessageStopEvent { StopReason = new("end_turn") }]
        };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        var events = new List<LlmStreamEvent>();
        Exception? failure = await Record.ExceptionAsync(async () =>
        {
            await foreach (LlmStreamEvent item in client.StreamAsync(ResourceConnection(streaming: true), Request() with { Resources = access }))
                events.Add(item);
        });
        Exception? retirement = await Record.ExceptionAsync(() => factory.DisposeAsync().AsTask());
        using MemoryStream retained = Assert.Single(access.Reads);
        Exception? release = failure;
        if (generationFails)
        {
            Exception[] causes = Assert.IsType<AggregateException>(failure).InnerExceptions.ToArray();
            Assert.Equal(2, causes.Length);
            Assert.Same(operation, Assert.IsType<InferenceResourceOperationException>(causes[0]).InnerException);
            release = causes[1];
        }
        InferenceResourceOperationException refused = Assert.IsType<InferenceResourceOperationException>(release);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, refused.Failure.Phase);
        Assert.Equal(reference, refused.Failure.Reference);
        Assert.Same(cleanup, refused.InnerException);
        Assert.Same(release, retirement);
        Assert.Same(retirement, await Record.ExceptionAsync(() => factory.DisposeAsync().AsTask()));
        Assert.DoesNotContain(events, item => item is LlmChatCompletionEvent);
        Assert.Equal(1, access.ReleaseAttempts);
        Assert.True(retained.CanRead);
        Assert.True(invoker.Disposed);
    }

    [Theory]
    [InlineData("instructions", true)]
    [InlineData("system", true)]
    [InlineData("developer", true)]
    [InlineData("removed-system", true)]
    [InlineData("appended-system", true)]
    [InlineData("unsigned-tail", false)]
    [InlineData("unsigned-context", false)]
    public async Task SignedCompactionPreservesTheCanonicalSystemPrompt(string change, bool rejected)
    {
        var control = new StreamControl();
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => control, _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker
        {
            Response = StubConverseInvoker.Converse(
            [
                new() { ReasoningContent = new() { ReasoningText = new() { Text = "reasoning", Signature = "signature" } } },
                new() { Text = "answer" }
            ], "end_turn")
        };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = Connection() with
        {
            ResolvedContinuationScope = "fixture-scope",
            CapabilityOverrides = new() { SupportsStreaming = InferenceSupport.Unsupported },
            Credential = new LlmCredential
            {
                Aws = new() { Region = "us-east-1", AccessKeyId = "fixture-key", SecretAccessKey = "fixture-secret" }
            }.Serialize()
        };
        LlmChatRequest initial = Request("instructions",
        [
            new("system", "system guidance"),
            new("developer", "developer guidance"),
            new("user", "hello")
        ]);
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, initial)) events.Add(item);
        LlmChatResult result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        Assert.NotNull(result.ContinuationState);
        LlmChatRequest before = initial with
        {
            Messages = [.. initial.Messages, new("assistant", result.Text) { ContinuationState = result.ContinuationState }, new("user", "next question")]
        };
        LlmChatMessage[] messages = before.Messages.ToArray();
        LlmChatRequest after = change switch
        {
            "instructions" => before with { Instructions = "changed instructions" },
            "system" => before with { Messages = [new("system", "changed guidance"), .. messages.Skip(1)] },
            "developer" => before with { Messages = [messages[0], new("developer", "changed guidance"), .. messages.Skip(2)] },
            "removed-system" => before with { Messages = messages.Skip(1).ToArray() },
            "appended-system" => before with { Messages = [.. messages, new("system", "new global guidance")] },
            "unsigned-tail" => before with { Messages = messages.Take(messages.Length - 1).ToArray() },
            "unsigned-context" => initial with { Instructions = "new instructions", Messages = [new("user", "new question")] },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        if (change == "unsigned-context") before = initial;

        ConverseRequest projected = BedrockLlmClient.BuildRequest(connection, before, before.ModelId);
        Assert.Collection(projected.System,
            block => Assert.Equal("instructions", block.Text),
            block => Assert.Equal("system guidance", block.Text),
            block => Assert.Equal("developer guidance", block.Text));
        if (change != "unsigned-context") Assert.Equal("signature", projected.Messages[1].Content[0].ReasoningContent.ReasoningText.Signature);
        client.ValidateCompaction(connection, before, before);
        Exception? error = Record.Exception(() => client.ValidateCompaction(connection, before, after));

        if (rejected) Assert.Contains("Start a fresh context", Assert.IsType<NotSupportedException>(error).Message);
        else Assert.Null(error);
        Assert.Single(invoker.ConverseRequests);
        Assert.Empty(invoker.StreamRequests);
    }

    [Theory]
    [InlineData("guardrail_intervened", true)]
    [InlineData("content_filtered", true)]
    [InlineData("end_turn", false)]
    public async Task AgentCoreGuardrailStopIsARefusalWithoutStructuredAnswerEvaluation(string stopReason, bool refused)
    {
        using var responseStream = new MemoryStream();
        (string Type, string Json)[] payloads =
        [
            ("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"{\"answer\":42}"}}"""),
            ("messageStop", $$"""{"stopReason":"{{stopReason}}"}"""),
            ("metadata", """{"usage":{"inputTokens":4,"outputTokens":6,"totalTokens":10}}""")
        ];
        foreach ((string type, string json) in payloads)
        {
            var messageType = new EventStreamHeader(":message-type");
            messageType.SetString("event");
            var eventType = new EventStreamHeader(":event-type");
            eventType.SetString(type);
            var contentType = new EventStreamHeader(":content-type");
            contentType.SetString("application/json");
            var message = new EventStreamMessage([messageType, eventType, contentType], System.Text.Encoding.UTF8.GetBytes(json));
            responseStream.Write(message.ToByteArray());
        }
        responseStream.Position = 0;
        var sdkClient = new AgentCoreStreamClient(new AgentCore.InvokeHarnessResponse
        {
            Stream = new AgentCore.InvokeHarnessStreamOutput(responseStream)
        });
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => sdkClient);
        var client = new BedrockAgentCoreLlmClient(new BedrockAgentCoreInvoker(factory));
        LlmConnection connection = Connection() with
        {
            Transport = LlmTransport.AwsBedrockAgentCore,
            Endpoint = "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/fixture",
            Credential = new LlmCredential
            {
                Aws = new() { Region = "us-east-1", AccessKeyId = "fixture-key", SecretAccessKey = "fixture-secret" }
            }.Serialize()
        };
        LlmChatRequest request = Request() with
        {
            ModelId = BedrockAgentCoreLlmClient.DefaultModelId,
            StructuredOutput = new()
            {
                Name = "answer",
                Schema = JsonElement.Parse("""{"type":"object","required":["answer"],"properties":{"answer":{"type":"integer"}}}"""),
                Fallback = LlmStructuredOutputFallback.LocalValidation
            }
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);
        LlmChatResult result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        byte[] jsonResult = JsonSerializer.SerializeToUtf8Bytes(result, ConnectLlmJson.ChatResult);
        LlmChatResult restored = JsonSerializer.Deserialize(jsonResult, ConnectLlmJson.ChatResult)!;

        Assert.Equal(refused ? LlmCompletionStatus.Refused : LlmCompletionStatus.Completed, restored.Status);
        Assert.Equal("{\"answer\":42}", restored.Text);
        Assert.Equal(refused ? restored.Text : null, restored.Refusal);
        Assert.Equal(refused ? "content_filter" : "stop", restored.FinishReason);
        Assert.Equal(!refused, restored.StructuredOutput!.WasEvaluated);
        Assert.Equal(!refused, restored.StructuredOutput.IsValid);
        Assert.Equal((4, 6, 10), (restored.Usage!.InputTokens, restored.Usage.OutputTokens, restored.Usage.TotalTokens));
        Assert.Null(restored.ContinuationState);
        Assert.Empty(restored.ToolCalls);
    }

    [Theory]
    [InlineData("service", false)]
    [InlineData("service", true)]
    [InlineData("client", false)]
    [InlineData("client", true)]
    [InlineData("http", false)]
    [InlineData("http", true)]
    [InlineData("wrapped-http", false)]
    [InlineData("wrapped-http", true)]
    [InlineData("acquisition", false)]
    [InlineData("acquisition", true)]
    public async Task AgentCoreSdkFailuresRetainSafeObservedDiagnostics(string kind, bool agent)
    {
        const string message = "Harness failed for fixture-key and fixture-secret.";
        var cause = new IOException("Private Harness graph fixture-secret.");
        Exception original = kind switch
        {
            "service" => new AmazonBedrockAgentCoreException(message, cause, ErrorType.Sender,
                "unavailable_fixture-key_fixture-secret", "request-fixture-key-fixture-secret", HttpStatusCode.ServiceUnavailable),
            "client" or "acquisition" => new AmazonClientException(message, cause),
            "wrapped-http" => new BedrockAgentCoreEventStreamException("Private Harness wrapper fixture-key.",
                new HttpRequestException(message, cause, HttpStatusCode.BadGateway)),
            _ => new HttpRequestException(message, cause, HttpStatusCode.BadGateway)
        };
        AgentCoreStreamClient? sdkClient = kind == "acquisition" ? null : new(new()) { Failure = original };
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => kind == "acquisition" ? throw original : sdkClient!);
        var client = new BedrockAgentCoreLlmClient(new BedrockAgentCoreInvoker(factory));
        LlmConnection connection = Connection() with
        {
            Transport = LlmTransport.AwsBedrockAgentCore,
            Endpoint = "arn:aws:bedrock-agentcore:us-east-1:123456789012:harness/fixture",
            Credential = new LlmCredential
            {
                Aws = new() { Region = "us-east-1", AccessKeyId = "fixture-key", SecretAccessKey = "fixture-secret" }
            }.Serialize()
        };
        int completions = 0;
        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(async () =>
        {
            if (agent)
            {
                await using ILlmAgentSession session = await client.StartSessionAsync("connection", connection, new()
                {
                    ModelId = BedrockAgentCoreLlmClient.DefaultModelId,
                    Runtime = new() { WorkingDirectory = AppContext.BaseDirectory }
                });
                await foreach (LlmAgentEvent item in session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")] }))
                    if (item is LlmAgentTurnCompletedEvent) completions++;
            }
            else
                await foreach (LlmStreamEvent item in client.StreamAsync(connection, Request() with { ModelId = BedrockAgentCoreLlmClient.DefaultModelId }))
                    if (item is LlmChatCompletionEvent) completions++;
        });
        Assert.Contains("Harness failed for [REDACTED] and [REDACTED].", failure.Message, StringComparison.Ordinal);
        Assert.Equal(LlmTransport.AwsBedrockAgentCore, failure.Transport);
        Assert.Equal(kind == "service" ? HttpStatusCode.ServiceUnavailable
            : kind is "http" or "wrapped-http" ? HttpStatusCode.BadGateway : (HttpStatusCode?)null, failure.StatusCode);
        Assert.Equal(kind == "service" ? "unavailable_[REDACTED]_[REDACTED]" : null, failure.ProviderCode);
        Assert.Equal(kind == "service" ? "request-[REDACTED]-[REDACTED]" : null, failure.RequestId);
        Assert.Null(failure.RetryAfter);
        foreach (string value in new[] { "fixture-key", "fixture-secret", "Private Harness" })
            Assert.DoesNotContain(value, failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, completions);
        Assert.Equal(kind == "acquisition" ? 0 : 1, sdkClient?.Calls ?? 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TerminalUsagePreservesTheReportedTotalInsteadOfReplacingIt(bool streaming, bool totalOnly)
    {
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => new StreamControl(), _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var usage = totalOnly ? new TokenUsage { TotalTokens = 10 } : new TokenUsage
        {
            InputTokens = 4,
            OutputTokens = 6,
            TotalTokens = 10,
            CacheReadInputTokens = 3,
            CacheWriteInputTokens = 2
        };
        var invoker = new StubConverseInvoker
        {
            Response = StubConverseInvoker.Converse([new() { Text = "answer" }], "end_turn", usage),
            Events = [new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { Text = "answer" } },
                new MessageStopEvent { StopReason = new("end_turn") }, new ConverseStreamMetadataEvent { Usage = usage }]
        };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = Connection() with
        {
            CapabilityOverrides = new() { SupportsStreaming = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported) },
            Credential = new LlmCredential
            {
                Aws = new() { Region = "us-east-1", AccessKeyId = "fixture-key", SecretAccessKey = "fixture-secret" }
            }.Serialize()
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, Request())) events.Add(item);
        LlmChatResult terminal = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        LlmChatResult restored = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(terminal,
            ConnectLlmJson.ChatResult), ConnectLlmJson.ChatResult)!;

        var expected = totalOnly ? new LlmUsage(null, null, 10) : new LlmUsage(9, 6, 10)
        { CachedInputTokens = 3, CacheWriteInputTokens = 2 };
        Assert.Equal(expected, restored.Usage);
        Assert.Equal("answer", restored.Text);
        Assert.Equal(LlmCompletionStatus.Completed, restored.Status);
        Assert.Equal(streaming ? 1 : 0, invoker.StreamRequests.Count);
        Assert.Equal(streaming ? 0 : 1, invoker.ConverseRequests.Count);
    }

    [Theory]
    [InlineData(false, "future_stop", LlmCompletionStatus.Unknown)]
    [InlineData(true, "future_stop", LlmCompletionStatus.Unknown)]
    [InlineData(false, "tool_use", LlmCompletionStatus.RequiresAction)]
    [InlineData(true, "tool_use", LlmCompletionStatus.RequiresAction)]
    public async Task UnknownStopsPreserveObservedToolsWithoutSuccessfulContinuation(
        bool streaming, string stopReason, LlmCompletionStatus expectedStatus)
    {
        var control = new StreamControl();
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => control, _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        const string partialText = "{\"partial\":";
        const string arguments = "{\"city\":\"Chicago\"}";
        var usage = new TokenUsage { InputTokens = 4, OutputTokens = 6, TotalTokens = 10 };
        var invoker = new StubConverseInvoker
        {
            Response = StubConverseInvoker.Converse(
            [
                new() { Text = partialText },
                new() { ReasoningContent = new() { ReasoningText = new() { Text = "reasoning", Signature = "signature" } } },
                new() { ToolUse = new() { ToolUseId = "tool-1", Name = "weather", Input = BedrockDocumentJson.Read(arguments) } }
            ], stopReason, usage),
            Events =
            [
                new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { Text = partialText } },
                new ContentBlockDeltaEvent { ContentBlockIndex = 1, Delta = new() { ReasoningContent = new() { Text = "reasoning" } } },
                new ContentBlockDeltaEvent { ContentBlockIndex = 1, Delta = new() { ReasoningContent = new() { Signature = "signature" } } },
                new ContentBlockStartEvent { ContentBlockIndex = 2, Start = new() { ToolUse = new() { ToolUseId = "tool-1", Name = "weather" } } },
                new ContentBlockDeltaEvent { ContentBlockIndex = 2, Delta = new() { ToolUse = new() { Input = arguments } } },
                new ContentBlockStopEvent { ContentBlockIndex = 2 },
                new MessageStopEvent { StopReason = new(stopReason) },
                new ConverseStreamMetadataEvent { Usage = usage }
            ]
        };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = Connection() with
        {
            ResolvedContinuationScope = "fixture-scope",
            CapabilityOverrides = new() { SupportsStreaming = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported) },
            Credential = new LlmCredential
            {
                Aws = new() { Region = "us-east-1", AccessKeyId = "fixture-key", SecretAccessKey = "fixture-secret" }
            }.Serialize()
        };
        LlmChatRequest request = Request() with
        {
            StructuredOutput = new()
            {
                Name = "answer",
                Schema = JsonElement.Parse("""{"type":"object"}"""),
                Fallback = LlmStructuredOutputFallback.LocalValidation
            }
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in client.StreamAsync(connection, request)) events.Add(item);
        LlmChatResult result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(result, ConnectLlmJson.ChatResult);
        LlmChatResult restored = JsonSerializer.Deserialize(json, ConnectLlmJson.ChatResult)!;

        Assert.Equal(expectedStatus, restored.Status);
        Assert.Equal(partialText, restored.Text);
        LlmToolCall call = Assert.IsType<LlmToolCall>(Assert.Single(restored.ToolCalls));
        Assert.Equal(("tool-1", "weather", arguments), (call.Id, call.Name, call.ArgumentsJson));
        Assert.Equal(call, Assert.Single(events.OfType<LlmToolCallCompletedEvent>()).ToolCall);
        Assert.Null(restored.StructuredOutput);
        Assert.Equal((4, 6, 10), (restored.Usage!.InputTokens, restored.Usage.OutputTokens, restored.Usage.TotalTokens));
        if (expectedStatus == LlmCompletionStatus.Unknown)
        {
            Assert.Equal(stopReason, restored.FinishReason);
            Assert.Null(restored.ContinuationState);
        }
        else Assert.NotNull(restored.ContinuationState);
        Assert.Equal(streaming ? 1 : 0, invoker.StreamRequests.Count);
        Assert.Equal(streaming ? 0 : 1, invoker.ConverseRequests.Count);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("{}", true)]
    [InlineData(" { } \n", true)]
    public async Task StreamedToolCallsRequireActualObjectInput(string? input, bool valid)
    {
        var control = new StreamControl();
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => control, _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var wire = new List<IEventStreamEvent>
        {
            new ContentBlockStartEvent
            {
                ContentBlockIndex = 0, Start = new() { ToolUse = new() { ToolUseId = "tool-1", Name = "weather" } }
            }
        };
        if (input is not null)
            wire.Add(new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { ToolUse = new() { Input = input } } });
        wire.Add(new ContentBlockStopEvent { ContentBlockIndex = 0 });
        wire.Add(new MessageStopEvent { StopReason = StopReason.Tool_use });
        var invoker = new StubConverseInvoker { Events = wire };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        var events = new List<LlmStreamEvent>();
        async Task ReadAsync()
        {
            await foreach (LlmStreamEvent item in client.StreamAsync(ResourceConnection(streaming: true), Request())) events.Add(item);
        }

        if (valid)
        {
            await ReadAsync();
            LlmChatResult result = Assert.IsType<LlmChatCompletionEvent>(events[^1]).Result;
            Assert.Equal(LlmCompletionStatus.RequiresAction, result.Status);
            var call = Assert.IsType<LlmToolCall>(Assert.Single(result.ToolCalls));
            Assert.Equal(("tool-1", "weather", input), (call.Id, call.Name, call.ArgumentsJson));
            Assert.Equal(call, Assert.Single(events.OfType<LlmToolCallCompletedEvent>()).ToolCall);
        }
        else
        {
            LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(ReadAsync);
            Assert.Equal("Bedrock returned tool input that is not a JSON object.", failure.Message);
            Assert.Equal("stream-request-1", failure.RequestId);
            Assert.Empty(events.OfType<LlmToolCallCompletedEvent>());
            Assert.Empty(events.OfType<LlmChatCompletionEvent>());
        }
        Assert.Equal("tool-1", Assert.Single(events.OfType<LlmToolCallStartedEvent>()).ToolCallId);
        Assert.Single(invoker.StreamRequests);
        Assert.Empty(invoker.ConverseRequests);
        Assert.True(invoker.Disposed);
    }

    [Fact]
    public async Task AgentCoreStreamFailureSurvivesFailedResponseCleanup()
    {
        var readFailure = new IOException("The fixture stream failed.");
        var cleanupFailure = new IOException("The fixture response could not be released.");
        using var responseStream = new FailingReadStream(readFailure);
        var sdkClient = new AgentCoreStreamClient(new FailingAgentCoreResponse(cleanupFailure)
        {
            Stream = new AgentCore.InvokeHarnessStreamOutput(responseStream)
        });
        var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => sdkClient);
        var invoker = new BedrockAgentCoreInvoker(factory);
        var aws = new LlmAwsCredentials { Region = "us-east-1" };

        Exception? operation = await Record.ExceptionAsync(async () =>
        {
            await foreach (IEventStreamEvent unused in invoker.InvokeAsync(
                Connection() with { Transport = LlmTransport.AwsBedrockAgentCore, Credential = new LlmCredential { Aws = aws }.Serialize() },
                aws, new AgentCore.InvokeHarnessRequest(), CancellationToken.None))
                throw new InvalidOperationException("The failed invocation must not emit an event.");
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Exception? retirement = await Record.ExceptionAsync(async () => await factory.DisposeAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Exception[] causes = Assert.IsType<AggregateException>(operation).Flatten().InnerExceptions.ToArray();
        Assert.Equal(2, causes.Length);
        var failure = Assert.IsType<LlmProviderException>(causes[0]);
        Assert.Equal(LlmTransport.AwsBedrockAgentCore, failure.Transport);
        Assert.Null(failure.StatusCode);
        Assert.Null(failure.InnerException?.InnerException);
        Assert.DoesNotContain(readFailure.Message, failure.ToString(), StringComparison.Ordinal);
        var cleanup = Assert.IsType<InferenceResourceOperationException>(causes[1]);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
        Assert.Same(cleanup, retirement);
        Assert.Same(retirement, await Record.ExceptionAsync(async () => await factory.DisposeAsync()));
        Assert.True(sdkClient.Disposed);
        Assert.False(responseStream.CanRead);
    }

    [Fact]
    public async Task PausedAgentCoreReaderDoesNotBlockFactoryRetirement()
    {
        var messageType = new EventStreamHeader(":message-type");
        messageType.SetString("event");
        var eventType = new EventStreamHeader(":event-type");
        eventType.SetString("contentBlockDelta");
        var contentType = new EventStreamHeader(":content-type");
        contentType.SetString("application/json");
        var message = new EventStreamMessage([messageType, eventType, contentType],
            """{"contentBlockIndex":0,"delta":{"text":"partial"}}"""u8.ToArray());
        using var responseStream = new MemoryStream(message.ToByteArray());
        var sdkClient = new AgentCoreStreamClient(new AgentCore.InvokeHarnessResponse
        {
            Stream = new AgentCore.InvokeHarnessStreamOutput(responseStream)
        });
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => throw new NotSupportedException(), _ => sdkClient);
        var invoker = new BedrockAgentCoreInvoker(factory);
        var aws = new LlmAwsCredentials
        {
            Region = "us-east-1",
            AccessKeyId = "fixture-key",
            SecretAccessKey = "fixture-secret"
        };
        await using IAsyncEnumerator<IEventStreamEvent> events = invoker.InvokeAsync(
            Connection() with { Transport = LlmTransport.AwsBedrockAgentCore, Credential = new LlmCredential { Aws = aws }.Serialize() },
            aws, new AgentCore.InvokeHarnessRequest(), CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("partial", Assert.IsType<AgentCore.HarnessContentBlockDeltaEvent>(events.Current).Delta.Text);
        await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sdkClient.Disposed);
        Assert.False(responseStream.CanRead);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task PausedConverseReaderDoesNotBlockFactoryRetirement()
    {
        var control = new StreamControl();
        await using var factory = new BedrockClientFactory(_ => throw new NotSupportedException(),
            _ => control, _ => throw new NotSupportedException());
        await using var guard = new BedrockLoggingGuard();
        var invoker = new StubConverseInvoker
        {
            Events =
            [
                new ContentBlockDeltaEvent { ContentBlockIndex = 0, Delta = new() { Text = "partial" } },
                new MessageStopEvent { StopReason = new("end_turn") }
            ]
        };
        var client = new BedrockLlmClient(factory, guard, invoker, NullLogger<BedrockLlmClient>.Instance);
        LlmConnection connection = Connection() with
        {
            Credential = new LlmCredential
            {
                Aws = new() { Region = "us-east-1", AccessKeyId = "fixture-key", SecretAccessKey = "fixture-secret" }
            }.Serialize()
        };
        await using IAsyncEnumerator<LlmStreamEvent> events = client.StreamAsync(connection, Request()).GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("partial", Assert.IsType<LlmTextDeltaEvent>(events.Current).Delta);
        await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(control.Disposed);
        Assert.True(invoker.Disposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.MoveNextAsync().AsTask());
    }

    [Fact]
    public void DocumentNamesAreUniqueAcrossPromptAndToolMessagesInBothConverseModes()
    {
        var attachment = new LlmAttachment
        {
            Kind = LlmBinaryKind.Document,
            FileName = "reference.txt",
            MediaType = "text/plain",
            Data = "reference"u8.ToArray()
        };
        LlmToolOutput output = new()
        {
            Parts = [new LlmToolOutputBinary(LlmBinaryKind.Document,
                "text/plain", "tool document"u8.ToArray(), "reference.txt")]
        };
        ConverseRequest request = BedrockLlmClient.BuildRequest(Connection(), Request(messages:
        [
            new("user", "Read both references") { Attachments = [attachment, attachment] },
            new("assistant", string.Empty)
            {
                ToolCalls = [new LlmToolCall("first", "read", "{}"), new LlmToolCall("second", "read", "{}")]
            },
            new("user", string.Empty)
            {
                ToolResults = [new LlmToolResult("first", output), new LlmToolResult("second", output)]
            },
            new("assistant", string.Empty) { ToolCalls = [new LlmToolCall("third", "read", "{}")] },
            new("user", string.Empty) { ToolResults = [new LlmToolResult("third", output)] }
        ]), "us.amazon.nova-lite-v1:0");
        foreach (List<Message> messages in new[] { request.Messages, BedrockLlmClient.ToStreamRequest(request).Messages })
        {
            DocumentBlock[] documents = messages.SelectMany(message => message.Content)
                .SelectMany(block => block.Document is { } document ? new[] { document }
                    : block.ToolResult?.Content.Where(part => part.Document is not null).Select(part => part.Document) ?? [])
                .ToArray();
            Assert.Equal(["reference", "reference", "tool document", "tool document", "tool document"],
                documents.Select(document => System.Text.Encoding.UTF8.GetString(document.Source.Bytes.ToArray())));
            Assert.Equal(documents.Length, documents.Select(document => document.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.All(documents, document =>
            {
                Assert.Equal(DocumentFormat.Txt, document.Format);
                Assert.Matches("^(prompt|tool-result)-document-[1-9][0-9]*$", document.Name);
            });
        }
    }

    [Fact]
    public void BuildRequestMapsToolDefinitionsCallsAndResults()
    {
        using JsonDocument schema = JsonDocument.Parse(
            """{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""");
        ConverseRequest request = BedrockLlmClient.BuildRequest(
            Connection(),
            Request(messages:
            [
                new LlmChatMessage("user", "Weather?"),
                new LlmChatMessage("assistant", string.Empty)
                {
                    ToolCalls = [new LlmToolCall("tool-1", "weather", "{\"city\":\"Chicago\"}")]
                },
                new LlmChatMessage("user", "Details attached.")
                {
                    ToolResults =
                    [
                        new LlmToolResult("tool-1", new LlmToolOutput
                        {
                            Parts =
                            [
                                new LlmToolOutputText("72 F"),
                                new LlmToolOutputBinary(
                                    LlmBinaryKind.Image,
                                    "image/png",
                                    new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a },
                                    "chart.png"),
                                new LlmToolOutputBinary(LlmBinaryKind.Audio, "audio/wav", new byte[] { 1 }, "wind.wav"),
                                new LlmToolOutputBinary(LlmBinaryKind.Document, "application/pdf", "%PDF-"u8.ToArray(), "report.pdf"),
                                new LlmToolOutputBinary(LlmBinaryKind.Document, "text/csv", "hour,f\n1,72"u8.ToArray(), "hours.csv")
                            ],
                            StructuredContent = JsonElement.Parse("""{"temperature_f":72}"""),
                            IsError = true
                        })
                    ]
                }
            ]) with
            {
                Tools =
                [
                    new LlmToolDefinition
                    {
                        Name = "weather",
                        Description = "Get weather.",
                        InputSchema = schema.RootElement.Clone(),
                        Strict = true
                    },
                    new LlmToolDefinition
                    {
                        Name = "notes",
                        Description = "Read notes.",
                        InputSchema = schema.RootElement.Clone()
                    }
                ],
                ToolChoice = new LlmToolChoice
                {
                    Mode = LlmToolChoiceMode.Specific,
                    Name = "weather"
                }
            },
            "model-x");

        Assert.Equal(2, request.ToolConfig.Tools.Count);
        ToolSpecification specification = request.ToolConfig.Tools[0].ToolSpec;
        Assert.Equal("weather", specification.Name);
        Assert.True(specification.Strict);
        // A non-strict tool leaves the field out; Nova rejects it even when false.
        Assert.Equal("notes", request.ToolConfig.Tools[1].ToolSpec.Name);
        Assert.Null(request.ToolConfig.Tools[1].ToolSpec.Strict);
        Assert.Equal("weather", request.ToolConfig.ToolChoice.Tool.Name);
        Assert.Equal("tool-1", request.Messages[1].Content[0].ToolUse.ToolUseId);
        Assert.Equal("Chicago", request.Messages[1].Content[0].ToolUse.Input.AsDictionary()["city"].AsString());
        Assert.Equal("tool-1", request.Messages[2].Content[0].ToolResult.ToolUseId);
        Assert.Equal(ToolResultStatus.Error, request.Messages[2].Content[0].ToolResult.Status);
        Assert.Equal(5, request.Messages[2].Content[0].ToolResult.Content.Count);
        Assert.Equal("72 F", request.Messages[2].Content[0].ToolResult.Content[0].Text);
        Assert.Equal(ImageFormat.Png, request.Messages[2].Content[0].ToolResult.Content[1].Image.Format);
        Assert.Equal(
            new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a },
            request.Messages[2].Content[0].ToolResult.Content[1].Image.Source.Bytes.ToArray());
        Assert.Equal(DocumentFormat.Pdf, request.Messages[2].Content[0].ToolResult.Content[2].Document.Format);
        Assert.Equal(
            "%PDF-"u8.ToArray(),
            request.Messages[2].Content[0].ToolResult.Content[2].Document.Source.Bytes.ToArray());
        DocumentBlock csv = request.Messages[2].Content[0].ToolResult.Content[3].Document;
        Assert.Equal((DocumentFormat.Csv, "tool-result-document-2"), (csv.Format, csv.Name));
        Assert.Equal(72, request.Messages[2].Content[0].ToolResult.Content[4].Json.AsDictionary()["temperature_f"].AsInt());
        Assert.Equal("Details attached.", request.Messages[2].Content[1].Text);
    }

    [Theory]
    [InlineData(new[] { "PROVISIONED" }, true)]
    [InlineData(new[] { "ON_DEMAND", "PROVISIONED" }, false)]
    [InlineData(new[] { "ON_DEMAND", "INFERENCE_PROFILE" }, false)]
    [InlineData(new[] { "INFERENCE_PROFILE" }, false)]
    [InlineData(new string[0], false)]
    [InlineData(null, false)]
    public void ModelListingOmitsOnlyProvisionedOnlyModels(string[]? inferenceTypes, bool expected) =>
        Assert.Equal(expected, BedrockModelResolver.IsProvisionedOnly(inferenceTypes));

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AwsBedrockConverse,
        Endpoint = "aws://bedrock"
    };

    private static LlmConnection ResourceConnection(bool streaming) => Connection() with
    {
        CapabilityOverrides = new() { SupportsStreaming = (streaming ? InferenceSupport.Supported : InferenceSupport.Unsupported) },
        Credential = new LlmCredential
        { Aws = new() { Region = "us-east-1", AccessKeyId = "fixture-key", SecretAccessKey = "fixture-secret" } }.Serialize()
    };

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Resources(InferenceResourceSelection selection,
        IReadOnlyList<(InferenceResourceReference Reference, string Name, string MediaType, byte[] Bytes)> files,
        Exception? releaseFailure = null) : IInferenceResourceAccess
    {
        public string WorkspaceId => selection.WorkspaceId;
        public string SessionId => selection.SessionId;
        public List<MemoryStream> Reads { get; } = [];
        public int ReleaseAttempts { get; private set; }
        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
        {
            var file = files.Single(item => item.Reference == reference);
            return Task.FromResult<InferenceResourceDescriptor?>(new(reference, SessionId, file.Name, file.MediaType, file.Bytes.Length));
        }
        public async Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default)
        {
            var stream = new MemoryStream(files.Single(item => item.Reference == reference).Bytes, writable: false);
            Reads.Add(stream);
            return new Read((await GetAsync(reference, cancellationToken))!, stream, this);
        }
        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => Task.FromResult(selection);
        public Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        private Exception? ReleaseFailure => releaseFailure;
        private sealed class Read(InferenceResourceDescriptor resource, MemoryStream stream, Resources owner) : IInferenceResourceReadHandle
        {
            public InferenceResourceDescriptor Resource => resource;
            public Stream Content => stream;
            public ValueTask DisposeAsync()
            {
                owner.ReleaseAttempts++;
                return owner.ReleaseFailure is { } failure ? ValueTask.FromException(failure) : stream.DisposeAsync();
            }
        }
    }

    private sealed class StreamControl()
        : AmazonBedrockClient(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1)
    {
        internal bool Disposed { get; private set; }
        internal Exception? LoggingFailure { get; init; }

        public override Task<ListFoundationModelsResponse> ListFoundationModelsAsync(
            ListFoundationModelsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListFoundationModelsResponse
            { ModelSummaries = [new() { ModelId = Request().ModelId, ModelName = "Fixture Claude", InferenceTypesSupported = ["ON_DEMAND"] }] });

        public override Task<ListInferenceProfilesResponse> ListInferenceProfilesAsync(
            ListInferenceProfilesRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListInferenceProfilesResponse { InferenceProfileSummaries = [] });

        public override Task<GetFoundationModelAvailabilityResponse> GetFoundationModelAvailabilityAsync(
            GetFoundationModelAvailabilityRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetFoundationModelAvailabilityResponse
            { AgreementAvailability = new() { Status = AgreementStatus.AVAILABLE }, AuthorizationStatus = AuthorizationStatus.AUTHORIZED });

        public override Task<GetFoundationModelResponse> GetFoundationModelAsync(
            GetFoundationModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetFoundationModelResponse { ModelDetails = new() { InferenceTypesSupported = ["ON_DEMAND"] } });

        public override Task<GetModelInvocationLoggingConfigurationResponse> GetModelInvocationLoggingConfigurationAsync(
            GetModelInvocationLoggingConfigurationRequest request, CancellationToken cancellationToken = default) =>
            LoggingFailure is { } failure
                ? Task.FromException<GetModelInvocationLoggingConfigurationResponse>(failure)
                : Task.FromResult(new GetModelInvocationLoggingConfigurationResponse());

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FailingReadStream(Exception readFailure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(readFailure);
    }

    private sealed class FailingAgentCoreResponse(Exception cleanupFailure) : AgentCore.InvokeHarnessResponse
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw cleanupFailure;
        }
    }

    private sealed class AgentCoreStreamClient(AgentCore.InvokeHarnessResponse response)
        : AmazonBedrockAgentCoreClient(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1)
    {
        internal bool Disposed { get; private set; }
        internal Exception? Failure { get; init; }
        internal int Calls { get; private set; }

        public override Task<AgentCore.InvokeHarnessResponse> InvokeHarnessAsync(
            AgentCore.InvokeHarnessRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Failure is { } failure ? Task.FromException<AgentCore.InvokeHarnessResponse>(failure) : Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static LlmChatRequest Request(
        string? instructions = null,
        IReadOnlyList<LlmChatMessage>? messages = null,
        int? maxTokens = null,
        double? temperature = null) => new()
        {
            ModelId = "anthropic.claude-haiku",
            Instructions = instructions,
            Messages = messages ?? [new LlmChatMessage("user", "hello")],
            MaxOutputTokens = maxTokens,
            Temperature = temperature
        };

}
