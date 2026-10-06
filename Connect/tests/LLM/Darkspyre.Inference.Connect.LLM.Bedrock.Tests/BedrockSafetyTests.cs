using System.Text.Json;
using System.Globalization;
using Amazon;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.BedrockAgentCore;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Amazon.Runtime.EventStreams;
using Darkspyre.Inference.Connect.LLM.Bedrock;
using Microsoft.Extensions.Logging.Abstractions;
using AgentCore = Amazon.BedrockAgentCore.Model;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockSafetyTests
{
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
            CapabilityOverrides = new() { SupportsStreaming = false },
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
            CapabilityOverrides = new() { SupportsStreaming = streaming },
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
                aws, new AgentCore.InvokeHarnessRequest(), CancellationToken.None))
                throw new InvalidOperationException("The failed invocation must not emit an event.");
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Exception? retirement = await Record.ExceptionAsync(async () => await factory.DisposeAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Exception[] causes = Assert.IsType<AggregateException>(operation).Flatten().InnerExceptions.ToArray();
        Assert.Equal(2, causes.Length);
        Assert.Same(readFailure, Assert.IsType<BedrockAgentCoreEventStreamException>(causes[0]).InnerException);
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

    private sealed class StreamControl()
        : AmazonBedrockClient(new BasicAWSCredentials("fixture-key", "fixture-secret"), RegionEndpoint.USEast1)
    {
        internal bool Disposed { get; private set; }

        public override Task<GetFoundationModelResponse> GetFoundationModelAsync(
            GetFoundationModelRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetFoundationModelResponse { ModelDetails = new() { InferenceTypesSupported = ["ON_DEMAND"] } });

        public override Task<GetModelInvocationLoggingConfigurationResponse> GetModelInvocationLoggingConfigurationAsync(
            GetModelInvocationLoggingConfigurationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetModelInvocationLoggingConfigurationResponse());

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

        public override Task<AgentCore.InvokeHarnessResponse> InvokeHarnessAsync(
            AgentCore.InvokeHarnessRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(response);

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
