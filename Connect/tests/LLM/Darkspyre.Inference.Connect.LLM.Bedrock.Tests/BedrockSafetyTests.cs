using System.Text.Json;
using System.Globalization;
using Amazon;
using Darkspyre.Inference.Common.LLM;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Darkspyre.Inference.Connect.LLM.Bedrock;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockSafetyTests
{
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
                            StructuredContent = JsonSerializer.SerializeToElement(new { temperature_f = 72 }),
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
