using System.Text.Json;
using System.Globalization;
using Amazon.Bedrock.Model;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Darkspyre.Inference.Connect.LLM.Bedrock;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockSafetyTests
{
    private const string Region = "us-east-1";

    [Fact]
    public void CapabilitiesAdvertiseHostExecutedTools()
    {
        var client = new BedrockLlmClient(new BedrockClientFactory(), new BedrockLoggingGuard());

        LlmProviderCapabilities capabilities = client.GetCapabilities(Connection());
        Assert.True(capabilities.SupportsToolUse);
        Assert.Equal(Common.LLM.LlmOutputLimitEnforcement.ProviderEnforced, capabilities.OutputLimitEnforcement);
        Assert.True(capabilities.SupportsPromptImages);
        Assert.True(capabilities.SupportsPromptDocuments);
        LlmRichContentSupported rich = capabilities.RichContentSupported;
        Assert.Equal(LlmRichContentCarriage.Native, rich.Images);
        Assert.Equal(LlmRichContentCarriage.Native, rich.PdfDocuments);
        Assert.Equal(LlmRichContentCarriage.Native, rich.TextDocuments);
        Assert.Equal(LlmRichContentCarriage.Native, rich.StructuredContent);
        Assert.Equal(LlmRichContentCarriage.Native, rich.ErrorFlag);
        Assert.Equal(LlmRichContentCarriage.Unsupported, rich.Audio);
        Assert.False(rich.AllowsEmpty);
    }

    [Fact]
    public void BuildRequestMapsMessagesSystemAndInferenceSettings()
    {
        ConverseRequest request = BedrockLlmClient.BuildRequest(
            Connection(),
            Request(
                instructions: "top-level system",
                messages:
                [
                    new LlmChatMessage("system", "inline system"),
                    new LlmChatMessage("developer", "inline developer"),
                    new LlmChatMessage("user", "first")
                    {
                        Attachments =
                        [
                            new LlmAttachment
                            {
                                Kind = LlmAttachmentKind.Image,
                                FileName = "chart.png",
                                Data = new byte[]
                                {
                                    0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a
                                }
                            },
                            new LlmAttachment
                            {
                                Kind = LlmAttachmentKind.Document,
                                FileName = "notes.txt",
                                Data = "document"u8.ToArray()
                            }
                        ]
                    },
                    new LlmChatMessage("assistant", "second")
                ],
                maxTokens: 128,
                temperature: 0.5),
            "model-x");

        Assert.Equal("model-x", request.ModelId);
        Assert.Equal(
            ["top-level system", "inline system", "inline developer"],
            request.System.Select(block => block.Text));
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal(ConversationRole.User, request.Messages[0].Role);
        Assert.Equal("first", request.Messages[0].Content[0].Text);
        Assert.NotNull(request.Messages[0].Content[1].Image);
        Assert.NotNull(request.Messages[0].Content[2].Document);
        Assert.Equal(ConversationRole.Assistant, request.Messages[1].Role);
        Assert.Equal(128, request.InferenceConfig.MaxTokens);
        Assert.Equal(0.5f, request.InferenceConfig.Temperature);
    }

    [Fact]
    public void BuildRequestRejectsUnsupportedProviderOptions()
    {
        using JsonDocument document = JsonDocument.Parse("true");
        LlmConnection connection = Connection() with
        {
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["unknown"] = document.RootElement.Clone()
            }
        };

        Assert.Throws<NotSupportedException>(() => BedrockLlmClient.BuildRequest(
            connection,
            Request(),
            "model-x"));
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
                        InputSchema = schema.RootElement.Clone()
                    },
                    new LlmToolDefinition
                    {
                        Name = "notes",
                        Description = "Read notes.",
                        InputSchema = schema.RootElement.Clone(),
                        Strict = false
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

    [Fact]
    public void AnEmptyToolOutputSendsOneEmptyTextBlock()
    {
        ConverseRequest request = BedrockLlmClient.BuildRequest(
            Connection(),
            Request(messages:
            [
                new LlmChatMessage("assistant", string.Empty) { ToolCalls = [new LlmToolCall("tool-1", "weather", "{}")] },
                new LlmChatMessage("user", string.Empty) { ToolResults = [new LlmToolResult("tool-1", new LlmToolOutput())] }
            ]),
            "model-x");

        ToolResultBlock result = request.Messages[1].Content[0].ToolResult;
        Assert.Equal(string.Empty, Assert.Single(result.Content).Text);
        Assert.Equal(ToolResultStatus.Success, result.Status);
    }

    [Fact]
    public void BuildRequestAttachesOnlyAGenericSequentialIdentifier()
    {
        ConverseRequest first = BedrockLlmClient.BuildRequest(
            Connection(),
            Request(
                "help the dungeon master",
                [new LlmChatMessage("user", "Describe Brandon's goblin ambush")]),
            "model-x");
        ConverseRequest second = BedrockLlmClient.BuildRequest(
            Connection(),
            Request(),
            "model-x");

        KeyValuePair<string, string> entry = Assert.Single(first.RequestMetadata);
        Assert.Equal(BedrockLlmClient.RequestIdMetadataKey, entry.Key);
        Assert.Matches("^request-[0-9]+$", entry.Value);
        Assert.DoesNotContain("Brandon", entry.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("goblin", entry.Value, StringComparison.OrdinalIgnoreCase);
        long firstNumber = long.Parse(
            entry.Value["request-".Length..],
            CultureInfo.InvariantCulture);
        long secondNumber = long.Parse(
            second.RequestMetadata[BedrockLlmClient.RequestIdMetadataKey]["request-".Length..],
            CultureInfo.InvariantCulture);
        Assert.Equal(firstNumber + 1, secondNumber);
    }

    [Theory]
    [InlineData("end_turn", "stop")]
    [InlineData("stop_sequence", "stop")]
    [InlineData("max_tokens", "length")]
    [InlineData("content_filtered", "content_filter")]
    [InlineData("guardrail_intervened", "content_filter")]
    [InlineData("tool_use", "tool_calls")]
    [InlineData("unknown", "unknown")]
    [InlineData(null, "stop")]
    public void MapFinishReasonPreservesKnownSemantics(string? reason, string expected) =>
        Assert.Equal(expected, BedrockLlmClient.MapFinishReason(reason));

    [Theory]
    [InlineData("arn:aws:bedrock:us-east-1:123:inference-profile/us.anthropic.claude", true)]
    [InlineData("us.anthropic.claude-haiku", true)]
    [InlineData("eu.anthropic.claude-sonnet", true)]
    [InlineData("apac.anthropic.claude-sonnet", true)]
    [InlineData("global.anthropic.claude-sonnet", true)]
    [InlineData("anthropic.claude-haiku", false)]
    public void ModelResolverRecognizesInferenceProfileIds(string modelId, bool expected) =>
        Assert.Equal(expected, BedrockModelResolver.LooksLikeInferenceProfile(modelId));

    [Theory]
    [InlineData("us-east-1", new[] { "us.", "global." })]
    [InlineData("eu-west-1", new[] { "eu.", "global." })]
    [InlineData("ap-southeast-2", new[] { "apac.", "global." })]
    [InlineData("ca-central-1", new[] { "global." })]
    public void ModelResolverPrefersRegionalProfiles(string region, string[] expected) =>
        Assert.Equal(expected, BedrockModelResolver.PreferredProfilePrefixes(region));

    [Fact]
    public void LoggingGuardDetectsDeliveryFlagsAndDestinations()
    {
        Assert.Null(BedrockLoggingGuard.DescribeEnabledLogging(null));
        Assert.Null(BedrockLoggingGuard.DescribeEnabledLogging(new LoggingConfig()));
        Assert.Contains("text", BedrockLoggingGuard.DescribeEnabledLogging(
            new LoggingConfig { TextDataDeliveryEnabled = true }));
        Assert.Contains("CloudWatch", BedrockLoggingGuard.DescribeEnabledLogging(
            new LoggingConfig
            {
                CloudWatchConfig = new CloudWatchConfig { LogGroupName = "bedrock-logs" }
            }));
        Assert.Contains("S3", BedrockLoggingGuard.DescribeEnabledLogging(
            new LoggingConfig { S3Config = new S3Config { BucketName = "bedrock-logs" } }));
    }

    [Fact]
    public async Task LoggingGuardCachesOnlyAllowedResultsWithinAnAccountScope()
    {
        var guard = new BedrockLoggingGuard();
        int safeFetches = 0;
        string safeScope = ScopeKey(profile: "safe-account");
        Task<LoggingConfig?> Safe(CancellationToken _)
        {
            safeFetches++;
            return Task.FromResult<LoggingConfig?>(null);
        }

        await guard.EnsureLoggingDisabledAsync(safeScope, Region, Safe);
        await guard.EnsureLoggingDisabledAsync(safeScope, Region, Safe);
        Assert.Equal(1, safeFetches);

        string unsafeScope = ScopeKey(profile: "logging-account");
        Assert.NotEqual(safeScope, unsafeScope);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.EnsureLoggingDisabledAsync(
                unsafeScope,
                Region,
                _ => Task.FromResult<LoggingConfig?>(
                    new LoggingConfig { TextDataDeliveryEnabled = true })));
    }

    [Fact]
    public async Task LoggingGuardDoesNotCacheARefusal()
    {
        var guard = new BedrockLoggingGuard();
        int fetches = 0;
        Task<LoggingConfig?> Fetch(CancellationToken _)
        {
            fetches++;
            return Task.FromResult<LoggingConfig?>(
                new LoggingConfig { TextDataDeliveryEnabled = true });
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.EnsureLoggingDisabledAsync("unsafe", Region, Fetch));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.EnsureLoggingDisabledAsync("unsafe", Region, Fetch));
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task LoggingGuardFailsClosedButPreservesCancellation()
    {
        var guard = new BedrockLoggingGuard();
        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            guard.EnsureLoggingDisabledAsync(
                "unverifiable",
                Region,
                _ => throw new InvalidOperationException("AccessDenied")));
        Assert.Contains("bedrock:GetModelInvocationLoggingConfiguration", failure.Message);
        Assert.Contains(Region, failure.Message);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            guard.EnsureLoggingDisabledAsync(
                "cancelled",
                Region,
                ct => Task.FromException<LoggingConfig?>(new OperationCanceledException(ct)),
                cancellation.Token));
    }

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AwsBedrockConverse,
        Endpoint = "aws://bedrock"
    };

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

    private static string ScopeKey(string? profile = null, string? accessKeyId = null) =>
        BedrockClientFactory.ScopeKey(new LlmAwsCredentials
        {
            Region = Region,
            Profile = profile,
            AccessKeyId = accessKeyId
        });
}
