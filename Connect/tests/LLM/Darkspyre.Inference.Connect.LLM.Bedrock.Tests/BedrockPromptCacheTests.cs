using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock.Tests;

public sealed class BedrockPromptCacheTests
{
    private const string Model = "us.anthropic.claude-sonnet-4-5-20250929-v1:0";
    private static LlmConnection Connection => new() { Version = "1", Transport = LlmTransport.AwsBedrockConverse, Endpoint = "https://bedrock.us-east-1.amazonaws.com" };
    private static LlmToolDefinition Tool => new() { Name = "run", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") };

    [Theory]
    [InlineData(LlmPromptCacheScope.Prefix, false)]
    [InlineData(LlmPromptCacheScope.Prefix, true)]
    [InlineData(LlmPromptCacheScope.Conversation, false)]
    [InlineData(LlmPromptCacheScope.Conversation, true)]
    public void CanonicalSystemAndMessagePointsReachBothResponseModes(LlmPromptCacheScope scope, bool extended)
    {
        LlmChatRequest request = Request() with
        {
            Instructions = "first",
            Messages = [new("system", "last"), new("user", "hello")],
            Tools = [Tool],
            PromptCache = new() { Scope = scope, Retention = extended ? LlmPromptCacheRetention.Extended : LlmPromptCacheRetention.Default }
        };
        ConverseRequest built = BedrockLlmClient.BuildRequest(Connection, request, Model);
        Assert.Equal(["first", "last"], built.System.Where(block => block.CachePoint is null).Select(block => block.Text));
        CachePointBlock prefix = Assert.Single(built.System, block => block.CachePoint is not null).CachePoint;
        Assert.Equal(CachePointType.Default, prefix.Type);
        Assert.Equal(extended ? CacheTTL.ONE_HOUR : CacheTTL.FIVE_MINUTES, prefix.Ttl);
        Assert.Null(Assert.Single(built.ToolConfig.Tools).CachePoint);
        Assert.Equal("hello", built.Messages[^1].Content[0].Text);
        if (scope == LlmPromptCacheScope.Conversation)
        {
            Assert.Equal(2, built.Messages[^1].Content.Count);
            Assert.Same(prefix, built.Messages[^1].Content[^1].CachePoint);
        }
        else { Assert.Single(built.Messages[^1].Content); }
        ConverseStreamRequest stream = BedrockLlmClient.ToStreamRequest(built);
        Assert.Same(built.System, stream.System);
        Assert.Same(built.ToolConfig, stream.ToolConfig);
        Assert.Same(built.Messages, stream.Messages);
    }

    [Theory]
    [InlineData(LlmPromptCacheScope.Prefix)]
    [InlineData(LlmPromptCacheScope.Conversation)]
    public void ToolOnlyPrefixAppendsOnePointAfterAllTools(LlmPromptCacheScope scope)
    {
        ConverseRequest built = BedrockLlmClient.BuildRequest(Connection,
            Request() with { Tools = [Tool, Tool with { Name = "second" }], PromptCache = new() { Scope = scope } }, Model);
        Assert.Null(built.System);
        Assert.Equal(["run", "second"], built.ToolConfig.Tools.Where(tool => tool.ToolSpec is not null).Select(tool => tool.ToolSpec.Name));
        Assert.Equal(3, built.ToolConfig.Tools.Count);
        Assert.Equal(CacheTTL.FIVE_MINUTES, built.ToolConfig.Tools[^1].CachePoint.Ttl);
        Assert.Equal(scope == LlmPromptCacheScope.Conversation ? 2 : 1, built.Messages[^1].Content.Count);
    }

    [Theory]
    [InlineData(LlmPromptCacheScope.Prefix, 1)]
    [InlineData(LlmPromptCacheScope.Conversation, 2)]
    public void MissingPrefixAddsNoSystemOrToolPoint(LlmPromptCacheScope scope, int messageBlocks)
    {
        ConverseRequest built = BedrockLlmClient.BuildRequest(Connection, Request() with { PromptCache = new() { Scope = scope } }, Model);
        Assert.Null(built.System);
        Assert.Null(built.ToolConfig);
        Assert.Equal(messageBlocks, built.Messages[^1].Content.Count);
    }

    [Theory]
    [InlineData("anthropic.claude-3-7-sonnet-20250219-v1:0", false)]
    [InlineData("eu.anthropic.claude-3-5-sonnet-20241022-v2:0", false)]
    [InlineData(Model, true)]
    [InlineData("global.anthropic.claude-opus-4-6-v1:0", true)]
    public void DeclaredRetentionsAndActualPointsUseTheSameModelClassification(string model, bool extended)
    {
        using var factory = new BedrockClientFactory();
        using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker());
        LlmPromptCacheCapabilities capabilities = client.GetCapabilities(Connection, model).PromptCache;
        Assert.False(capabilities.Automatic);
        Assert.True(capabilities.AcceptsHints);
        Assert.Contains(LlmPromptCacheRetention.Default, capabilities.Retentions);
        Assert.Equal(extended, capabilities.Retentions.Contains(LlmPromptCacheRetention.Extended));
        ConverseRequest built = BedrockLlmClient.BuildRequest(Connection, Request() with
        { ModelId = model, Instructions = "prefix", PromptCache = new() { Retention = LlmPromptCacheRetention.Extended } }, model);
        Assert.Equal(extended ? CacheTTL.ONE_HOUR : CacheTTL.FIVE_MINUTES, built.System[^1].CachePoint.Ttl);
        Assert.Equal(built.System[^1].CachePoint.Ttl, built.Messages[^1].Content[^1].CachePoint.Ttl);
    }

    [Theory]
    [InlineData("unknown.model")]
    [InlineData("openai.gpt-oss-120b-1:0")]
    [InlineData("deepseek.r1-v1:0")]
    [InlineData("arn:aws:bedrock:us-east-1:123:inference-profile/claude")]
    public void OtherFamiliesIgnoreHintsWithoutChangingThePrompt(string model)
    {
        ConverseRequest built = BedrockLlmClient.BuildRequest(Connection, Request() with
        { ModelId = model, Instructions = "prefix", Tools = [Tool], PromptCache = new() { Retention = LlmPromptCacheRetention.Extended } }, model);
        Assert.Null(Assert.Single(built.System).CachePoint);
        Assert.Null(Assert.Single(built.ToolConfig.Tools).CachePoint);
        Assert.Null(Assert.Single(built.Messages[^1].Content).CachePoint);
        Assert.False(BedrockPromptCache.Capabilities(model).AcceptsHints);
    }

    [Fact]
    public void HintsDoNotInventInputOccupancyOrEnableUnknownRetentions()
    {
        using var factory = new BedrockClientFactory();
        using var guard = new BedrockLoggingGuard();
        var client = new BedrockLlmClient(factory, guard, new StubConverseInvoker());
        LlmChatRequest request = Request() with { Instructions = "prefix" };
        LlmChatRequest hinted = request with { PromptCache = new() { Retention = (LlmPromptCacheRetention)999 } };
        Assert.Equal(client.EstimateInputTokens(Connection, request), client.EstimateInputTokens(Connection, hinted));
        ConverseRequest built = BedrockLlmClient.BuildRequest(Connection, hinted, Model);
        Assert.Equal(CacheTTL.FIVE_MINUTES, built.System[^1].CachePoint.Ttl);
        Assert.Equal(CacheTTL.FIVE_MINUTES, built.Messages[^1].Content[^1].CachePoint.Ttl);
    }

    [Fact]
    public void NoHintAddsNoMarkersAndRetainedSignedBlocksStayUnchanged()
    {
        JsonArray blocks = JsonNode.Parse("""[{"type":"reasoning","text":"private","signature":"signed"},{"type":"text","text":"answer"}]""")!.AsArray();
        LlmContinuationState state = ProviderContinuationStateCodec.Encode(Connection, Model, blocks);
        LlmChatRequest request = Request() with
        { Instructions = "prefix", Tools = [Tool], Messages = [new("user", "hello"), new("assistant", "answer") { ContinuationState = state }] };
        string retained = state.Payload;
        ConverseRequest plain = BedrockLlmClient.BuildRequest(Connection, request, Model);
        Assert.Null(Assert.Single(plain.System).CachePoint);
        Assert.Null(Assert.Single(plain.ToolConfig.Tools).CachePoint);
        Assert.All(plain.Messages.SelectMany(message => message.Content), block => Assert.Null(block.CachePoint));
        ConverseRequest cached = BedrockLlmClient.BuildRequest(Connection, request with { PromptCache = new() }, Model);
        Assert.Equal(retained, state.Payload);
        Assert.Equal(3, cached.Messages[^1].Content.Count);
        Assert.Equal(("private", "signed"), (cached.Messages[^1].Content[0].ReasoningContent.ReasoningText.Text, cached.Messages[^1].Content[0].ReasoningContent.ReasoningText.Signature));
        Assert.Equal("answer", cached.Messages[^1].Content[1].Text);
        Assert.Null(cached.Messages[^1].Content[0].CachePoint);
        Assert.Null(cached.Messages[^1].Content[1].CachePoint);
        Assert.NotNull(cached.Messages[^1].Content[2].CachePoint);
    }

    private static LlmChatRequest Request() => new() { ModelId = Model, Messages = [new("user", "hello")] };
}
