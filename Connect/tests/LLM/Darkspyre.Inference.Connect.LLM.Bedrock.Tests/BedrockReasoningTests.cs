using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.BedrockRuntime.Model;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockReasoningTests : IDisposable
{
    private const string ClaudeSonnet5 = "us.anthropic.claude-sonnet-5-v1:0";
    private const string ClaudeSonnet45 = "us.anthropic.claude-sonnet-4-5-20250929-v1:0";
    private const string Nova2 = "amazon.nova-2-lite-v1:0";
    private const string GptOss = "openai.gpt-oss-120b-1:0";

    private readonly BedrockClientFactory _factory = new();
    private readonly BedrockLoggingGuard _guard = new();
    private readonly BedrockLlmClient _client;

    public BedrockReasoningTests() => _client = new(_factory, _guard, new StubConverseInvoker());

    public void Dispose()
    {
        _guard.Dispose();
        _factory.Dispose();
    }

    [Theory]
    [InlineData(ClaudeSonnet45, nameof(BedrockModelFamily.AnthropicClaude), "anthropic.claude-sonnet-4-5-20250929-v1:0")]
    [InlineData("global.anthropic.claude-opus-4-1-20250805-v1:0", nameof(BedrockModelFamily.AnthropicClaude), "anthropic.claude-opus-4-1-20250805-v1:0")]
    [InlineData(Nova2, nameof(BedrockModelFamily.AmazonNova2), Nova2)]
    [InlineData(GptOss, nameof(BedrockModelFamily.OpenAiGptOss), GptOss)]
    [InlineData("deepseek.r1-v1:0", nameof(BedrockModelFamily.DeepSeekR1), "deepseek.r1-v1:0")]
    [InlineData("amazon.nova-lite-v1:0", nameof(BedrockModelFamily.Unknown), "amazon.nova-lite-v1:0")]
    [InlineData("arn:aws:bedrock:us-east-1:123:inference-profile/us.anthropic.claude-sonnet-5-v1:0", nameof(BedrockModelFamily.Unknown),
        "arn:aws:bedrock:us-east-1:123:inference-profile/us.anthropic.claude-sonnet-5-v1:0")]
    public void ClassifiesModelAndInferenceProfileIds(string modelId, string family, string baseModelId) =>
        Assert.Equal((Enum.Parse<BedrockModelFamily>(family), baseModelId), BedrockModelFamilies.Classify(modelId));

    [Theory]
    [InlineData("anthropic.claude-sonnet-4-5-20250929-v1:0", "claude-sonnet-4-5")]
    [InlineData("anthropic.claude-3-7-sonnet-20250219-v1:0", "claude-3-7-sonnet")]
    [InlineData("anthropic.claude-sonnet-5-v1:0", "claude-sonnet-5")]
    [InlineData("anthropic.claude-opus-5-5", "claude-opus-5-5")]
    public void DerivesTheAnthropicModelId(string baseModelId, string anthropicModelId) =>
        Assert.Equal(anthropicModelId, BedrockModelFamilies.AnthropicModelId(baseModelId));

    [Fact]
    public void SamplingUsesConverseAndFamilyFieldsWithoutReplacingReasoning()
    {
        var request = Request(ClaudeSonnet45) with
        { TopP = .97, TopK = 17, StopSequences = ["stop"], ReasoningLevel = LlmReasoningLevel.None };
        ConverseRequest built = BedrockLlmClient.BuildRequest(Connection(), request, ClaudeSonnet45);
        Assert.Equal(.97f, built.InferenceConfig.TopP);
        Assert.Equal(["stop"], built.InferenceConfig.StopSequences);
        JsonNode fields = JsonNode.Parse(JsonSerializer.Serialize(built.AdditionalModelRequestFields))!;
        Assert.Equal(17, (int?)fields["top_k"]);
        Assert.Null(fields["thinking"]);
        ConverseRequest thinking = BedrockLlmClient.BuildRequest(Connection(), request with
        { TopK = null, ReasoningLevel = LlmReasoningLevel.Medium }, ClaudeSonnet45);
        JsonNode thinkingFields = JsonNode.Parse(JsonSerializer.Serialize(thinking.AdditionalModelRequestFields))!;
        Assert.Equal("enabled", (string?)thinkingFields["thinking"]?["type"]);
        Assert.Equal(4096, (int?)thinkingFields["thinking"]?["budget_tokens"]);
        Assert.Equal(.97f, thinking.InferenceConfig.TopP);
        ConverseStreamRequest streamed = BedrockLlmClient.ToStreamRequest(built);
        Assert.Same(built.InferenceConfig, streamed.InferenceConfig);
        Assert.Equal(built.AdditionalModelRequestFields, streamed.AdditionalModelRequestFields);
        ConverseRequest nova = BedrockLlmClient.BuildRequest(Connection(), request with
        { ModelId = Nova2, ReasoningLevel = LlmReasoningLevel.Medium }, Nova2);
        JsonNode novaFields = JsonNode.Parse(JsonSerializer.Serialize(nova.AdditionalModelRequestFields))!;
        Assert.Equal(17, (int?)novaFields["inferenceConfig"]?["topK"]);
        Assert.Equal("medium", (string?)novaFields["reasoningConfig"]?["maxReasoningEffort"]);
    }

    [Fact]
    public void UnsupportedSamplingAndNativeLimitsFailBeforeAwsResolution()
    {
        Assert.Throws<NotSupportedException>(() => _client.EstimateInputTokens(Connection(), Request(GptOss) with { TopK = 17 }));
        Assert.Throws<NotSupportedException>(() => _client.GetCapabilities(Connection() with { CapabilityOverrides = new() { SupportsTopK = true } }, GptOss));
        Assert.Throws<NotSupportedException>(() => _client.EstimateInputTokens(Connection(), Request(Nova2) with { Seed = 0 }));
        Assert.Throws<NotSupportedException>(() => _client.EstimateInputTokens(Connection(), Request(Nova2) with { PresencePenalty = 0 }));
        Assert.Throws<NotSupportedException>(() => _client.EstimateInputTokens(Connection(), Request(Nova2) with { FrequencyPenalty = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _client.EstimateInputTokens(Connection(), Request(Nova2) with { TopK = 129 }));
        Assert.Throws<NotSupportedException>(() => _client.EstimateInputTokens(Connection(), Request(Nova2) with { TopK = 17, ReasoningLevel = LlmReasoningLevel.High }));
        Assert.Throws<NotSupportedException>(() => _client.EstimateInputTokens(Connection(), Request(Nova2) with { TopP = .97, ReasoningLevel = LlmReasoningLevel.High }));
        Assert.Throws<NotSupportedException>(() => _client.EstimateInputTokens(Connection(), Request("deepseek.r1-v1:0") with { StopSequences = Enumerable.Range(0, 11).Select(i => "stop" + i).ToArray() }));
        Assert.Equal(2500, _client.GetCapabilities(Connection(), ClaudeSonnet45).MaxStopSequences);
        Assert.Equal(10, _client.GetCapabilities(Connection(), "deepseek.r1-v1:0").MaxStopSequences);
    }

    [Fact]
    public void EachFamilyOffersItsLevels()
    {
        Assert.Equal(
            [LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High,
             LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            Levels(ClaudeSonnet5));
        Assert.Equal(
            [LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh, LlmReasoningLevel.Maximum],
            Levels("anthropic.claude-sonnet-5-5-v1:0"));
        Assert.Equal([LlmReasoningLevel.None, LlmReasoningLevel.Low, LlmReasoningLevel.Medium, LlmReasoningLevel.High], Levels(Nova2));
        Assert.Empty(Levels(GptOss));
        Assert.Empty(Levels("deepseek.r1-v1:0"));
        Assert.Empty(_client.GetCapabilities(Connection()).ReasoningLevels);
    }

    [Fact]
    public void AnOverrideNarrowsTheFamilyLevelsButCannotWidenThem()
    {
        LlmConnection narrowed = Connection() with
        {
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
            {
                [Nova2] = new() { ReasoningLevels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.Low } }
            }
        };
        LlmConnection widened = Connection() with
        {
            ModelCapabilityOverrides = new Dictionary<string, LlmCapabilityOverrides>
            {
                [Nova2] = new() { ReasoningLevels = new HashSet<LlmReasoningLevel> { LlmReasoningLevel.Maximum } }
            }
        };

        Assert.Equal([LlmReasoningLevel.Low], _client.GetCapabilities(narrowed, Nova2).ReasoningLevels);
        Assert.Throws<NotSupportedException>(() => _client.GetCapabilities(widened, Nova2));
    }

    [Theory]
    [InlineData(ClaudeSonnet5, LlmReasoningLevel.High, """{"thinking":{"type":"adaptive"},"output_config":{"effort":"high"}}""")]
    [InlineData(ClaudeSonnet5, LlmReasoningLevel.None, """{"thinking":{"type":"disabled"}}""")]
    [InlineData(ClaudeSonnet45, LlmReasoningLevel.Medium, """{"thinking":{"type":"enabled","budget_tokens":4096}}""")]
    [InlineData(Nova2, LlmReasoningLevel.None, """{"reasoningConfig":{"type":"disabled"}}""")]
    [InlineData(Nova2, LlmReasoningLevel.Medium, """{"reasoningConfig":{"type":"enabled","maxReasoningEffort":"medium"}}""")]
    public void BuildRequestSendsTheFamilysAdditionalFields(string modelId, LlmReasoningLevel level, string expected)
    {
        ConverseRequest built = BedrockLlmClient.BuildRequest(Connection(), Request(modelId) with { ReasoningLevel = level }, modelId);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(expected),
            JsonNode.Parse(JsonSerializer.Serialize(built.AdditionalModelRequestFields))));
    }

    [Fact]
    public void AClaudeBudgetRaisesMaxTokensAndStreamingCarriesTheFields()
    {
        ConverseRequest defaulted = BedrockLlmClient.BuildRequest(
            Connection(), Request(ClaudeSonnet45) with { ReasoningLevel = LlmReasoningLevel.Medium }, ClaudeSonnet45);
        ConverseRequest capped = BedrockLlmClient.BuildRequest(
            Connection(), Request(ClaudeSonnet45) with { ReasoningLevel = LlmReasoningLevel.Low, MaxOutputTokens = 2000 }, ClaudeSonnet45);
        ConverseRequest none = BedrockLlmClient.BuildRequest(Connection(), Request(GptOss), GptOss);

        Assert.Equal(8192 + 4096, defaulted.InferenceConfig.MaxTokens);
        Assert.Equal(2000 + 1024, capped.InferenceConfig.MaxTokens);
        Assert.True(BedrockLlmClient.ToStreamRequest(defaulted).AdditionalModelRequestFields.Equals(defaulted.AdditionalModelRequestFields));
        Assert.True(none.AdditionalModelRequestFields.IsNull());
        Assert.Null(none.InferenceConfig);
    }

    [Fact]
    public void ClaudeThinkingAndNovaHighRejectConflictingControlsBeforeTheCall()
    {
        LlmToolDefinition tool = new()
        {
            Name = "read_file",
            Description = "Reads one file.",
            InputSchema = JsonElement.Parse("""{"type":"object"}""")
        };

        NotSupportedException claudeTemperature = Assert.Throws<NotSupportedException>(() => _client.EstimateInputTokens(Connection(),
            Request(ClaudeSonnet5) with { ReasoningLevel = LlmReasoningLevel.High, Temperature = 0.5 }));
        Assert.Throws<NotSupportedException>(() => _client.EstimateInputTokens(Connection(), Request(ClaudeSonnet5) with
        {
            ReasoningLevel = LlmReasoningLevel.Low,
            Tools = [tool],
            ToolChoice = new LlmToolChoice { Mode = LlmToolChoiceMode.Required }
        }));
        Assert.Throws<NotSupportedException>(() => _client.EstimateInputTokens(Connection(),
            Request(ClaudeSonnet5) with { ReasoningLevel = LlmReasoningLevel.None, Temperature = 0.5 }));
        Assert.NotNull(_client.EstimateInputTokens(Connection(),
            Request(ClaudeSonnet45) with { ReasoningLevel = LlmReasoningLevel.None, Temperature = 0.5 }));
        NotSupportedException novaTemperature = Assert.Throws<NotSupportedException>(() => _client.EstimateInputTokens(Connection(),
            Request(Nova2) with { ReasoningLevel = LlmReasoningLevel.High, Temperature = 0.5 }));
        Assert.Throws<NotSupportedException>(() => _client.EstimateInputTokens(Connection(),
            Request(Nova2) with { ReasoningLevel = LlmReasoningLevel.High, MaxOutputTokens = 100 }));
        Assert.NotNull(_client.EstimateInputTokens(Connection(),
            Request(Nova2) with { ReasoningLevel = LlmReasoningLevel.Medium, Temperature = 0.5, MaxOutputTokens = 100 }));

        Assert.Contains("temperature", claudeTemperature.Message, StringComparison.Ordinal);
        Assert.Contains("top-p", novaTemperature.Message, StringComparison.Ordinal);
    }

    private IEnumerable<LlmReasoningLevel> Levels(string modelId) =>
        _client.GetCapabilities(Connection(), modelId).ReasoningLevels.Order();

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AwsBedrockConverse,
        Endpoint = "aws://bedrock"
    };

    private static LlmChatRequest Request(string modelId) => new()
    {
        ModelId = modelId,
        Messages = [new LlmChatMessage("user", "hello")]
    };
}
