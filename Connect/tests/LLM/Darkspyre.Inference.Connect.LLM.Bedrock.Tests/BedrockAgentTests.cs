using Darkspyre.Inference.Common.LLM;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Darkspyre.Inference.Connect.LLM.Bedrock;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockAgentTests
{
    private const string Model = "amazon.nova-lite-v1:0";

    private readonly BedrockLlmClient _client = new(new BedrockClientFactory(), new BedrockLoggingGuard());

    [Fact]
    public void CapabilitiesOpenTheManagedAgentLaneOnTheConverseRoute()
    {
        LlmProviderCapabilities capabilities = _client.GetCapabilities(Connection(), Model);

        Assert.True(capabilities.SupportsAgentSessions);
        Assert.True(capabilities.SupportsAgentToolUse);
        Assert.True(capabilities.SupportsAgentTurnInstructions);
        Assert.Equal(capabilities.RichContentSupported, capabilities.Agent!.RichContentSupported);
    }

    [Fact]
    public void AReplyWithReportedUsageCarriesItsCountsAsContinuationState()
    {
        LlmChatResult result = Complete(Response(new TokenUsage { InputTokens = 1200, OutputTokens = 80, TotalTokens = 1280 }));

        LlmContinuationState state = Assert.IsType<LlmContinuationState>(result.ContinuationState);
        Assert.Equal((ProviderContinuationStateCodec.ImplementationId(LlmTransport.AwsBedrockConverse), Model), (state.ImplementationId, state.ModelId));
        Assert.Equal((1200L, 80L), (state.InputTokens, state.OutputTokens));
        Assert.Equal("[]", state.Payload);
        Assert.Null(Complete(Response(usage: null)).ContinuationState);
    }

    [Fact]
    public void AHistoryWithoutReportsIsEstimatedWholeIncludingTools()
    {
        LlmChatRequest bare = Request([new LlmChatMessage("user", "hello")]);
        ContextEstimate estimate = _client.EstimateInputTokens(Connection(), bare)!.Value;
        ContextEstimate withTools = _client.EstimateInputTokens(Connection(), bare with
        {
            Tools = [new LlmToolDefinition { Name = "read_file", Description = "Reads one file from the workspace.", InputSchema = Schema() }]
        })!.Value;

        Assert.False(estimate.IncludesProviderReport);
        Assert.True(estimate.Tokens > 0);
        Assert.True(withTools.Tokens > estimate.Tokens);
        Assert.NotNull(_client.EstimateDiagnostic);
    }

    [Fact]
    public void AReportCountsTheHistoryItClosesAndOnlyTheTailIsTokenized()
    {
        LlmChatMessage reported = new("assistant", "done") { ContinuationState = Counts(1200, 80) };
        LlmChatMessage[] history = [new("user", "first"), reported];

        ContextEstimate closed = _client.EstimateInputTokens(Connection(), Request(history))!.Value;
        ContextEstimate open = _client.EstimateInputTokens(Connection(), Request([.. history, new("user", "and then")]))!.Value;

        Assert.Equal(new ContextEstimate(1280, IncludesProviderReport: true), closed);
        Assert.True(open.IncludesProviderReport);
        Assert.True(open.Tokens > 1280);
    }

    [Fact]
    public void AnAttachmentAfterTheLastReportLeavesTheRequestUnestimated()
    {
        LlmChatMessage image = new("user", "look") { Attachments = [Png()] };
        LlmChatMessage reported = new("assistant", "done") { ContinuationState = Counts(1200, 80) };

        Assert.Null(_client.EstimateInputTokens(Connection(), Request([image])));
        Assert.Null(_client.EstimateInputTokens(Connection(), Request([reported, image])));
        Assert.Equal(1280, _client.EstimateInputTokens(Connection(), Request([image, reported]))!.Value.Tokens);
    }

    [Fact]
    public void OnlyThisConnectionsOwnStateIsAccepted()
    {
        LlmChatMessage foreign = new("assistant", "done") { ContinuationState = Counts(1200, 80) with { ModelId = "another-model" } };
        LlmChatMessage other = new("assistant", "done") { ContinuationState = Counts(1200, 80) with { ImplementationId = ProviderContinuationStateCodec.ImplementationId(LlmTransport.OpenAiResponses) } };

        Assert.Throws<ArgumentException>(() => _client.EstimateInputTokens(Connection(), Request([foreign])));
        Assert.Throws<ArgumentException>(() => _client.EstimateInputTokens(Connection(), Request([other])));
    }

    [Fact]
    public void ARequestedReasoningLevelFailsBeforeTheCallAndOverridesCannotOfferOne()
    {
        var overridden = Connection() with
        {
            CapabilityOverrides = new() { ReasoningLevels = new HashSet<Common.LLM.LlmReasoningLevel> { Common.LLM.LlmReasoningLevel.High } }
        };

        Assert.Empty(_client.GetCapabilities(overridden, Model).ReasoningLevels);
        Assert.Throws<Common.InferenceException>(() => _client.EstimateInputTokens(overridden,
            Request([new("user", "hello")]) with { ReasoningLevel = Common.LLM.LlmReasoningLevel.High }));
    }

    private static LlmChatResult Complete(ConverseResponse response) =>
        Assert.IsType<LlmChatCompletionEvent>(BedrockLlmClient.MapResponse(Connection(), Request([new("user", "hello")]), null, response).Last()).Result;

    private static LlmContinuationState Counts(long input, long output) =>
        Complete(Response(new TokenUsage { InputTokens = (int)input, OutputTokens = (int)output, TotalTokens = (int)(input + output) })).ContinuationState!;

    private static ConverseResponse Response(TokenUsage? usage) => new()
    {
        Output = new ConverseOutput
        {
            Message = new Message { Role = ConversationRole.Assistant, Content = [new ContentBlock { Text = "done" }] }
        },
        StopReason = StopReason.End_turn,
        Usage = usage
    };

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.AwsBedrockConverse,
        Endpoint = "aws://bedrock"
    };

    private static LlmChatRequest Request(IReadOnlyList<LlmChatMessage> messages) => new()
    {
        ModelId = Model,
        Instructions = "Answer briefly.",
        Messages = messages
    };

    private static System.Text.Json.JsonElement Schema() => System.Text.Json.JsonElement.Parse(
        """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");

    private static LlmAttachment Png() => new()
    {
        Kind = LlmBinaryKind.Image,
        FileName = "chart.png",
        Data = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }
    };
}
