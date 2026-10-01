using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class PromptCacheContractTests
{
    [Fact]
    public void HintDefaultsToConversationWithDefaultRetentionAndCapabilityDefaultsToNone()
    {
        var hint = new LlmPromptCacheHint();
        Assert.Equal(LlmPromptCacheScope.Conversation, hint.Scope);
        Assert.Equal(LlmPromptCacheRetention.Default, hint.Retention);
        Assert.Null(new LlmInferenceRequest { ModelId = "model" }.PromptCache);
        Assert.Null(new LlmChatRequest { ModelId = "model" }.PromptCache);
        Assert.Equal(new LlmPromptCacheCapabilities(), new LlmCapabilities().PromptCache);
        Assert.Equal(new LlmPromptCacheCapabilities(), new LlmProviderCapabilities().PromptCache);
        Assert.Empty(new LlmProviderCapabilities().PromptCache.Retentions);
    }

    [Fact]
    public void CapabilityOverridesPreserveTheAdaptersCacheDeclaration()
    {
        var cache = new LlmPromptCacheCapabilities
        {
            Automatic = true,
            AcceptsHints = true,
            Retentions = new[] { LlmPromptCacheRetention.Default, LlmPromptCacheRetention.Extended }.ToFrozenSet()
        };
        var capabilities = new LlmProviderCapabilities { PromptCache = cache };
        Assert.Same(cache, capabilities.Apply(null).PromptCache);
        Assert.Same(cache, capabilities.Apply(new() { SupportsStreaming = false }).PromptCache);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(999, 999)]
    public void OwnedMetadataPreservesHintEnumsOnBothRequestContracts(int scope, int retention)
    {
        string json = $$$"""{"ModelId":"model","PromptCache":{"Scope":{{{scope}}},"Retention":{{{retention}}}}}""";
        LlmInferenceRequest common = JsonSerializer.Deserialize(json, InferenceJson.LlmRequest)!;
        LlmChatRequest connect = JsonSerializer.Deserialize(json, ConnectLlmJson.ChatRequest)!;
        Assert.Equal((LlmPromptCacheScope)scope, common.PromptCache!.Scope);
        Assert.Equal((LlmPromptCacheRetention)retention, common.PromptCache.Retention);
        Assert.Equal(common.PromptCache, connect.PromptCache);
        using JsonDocument commonWritten = JsonDocument.Parse(JsonSerializer.Serialize(common, InferenceJson.LlmRequest));
        using JsonDocument connectWritten = JsonDocument.Parse(JsonSerializer.Serialize(connect, ConnectLlmJson.ChatRequest));
        Assert.Equal(commonWritten.RootElement.GetProperty("PromptCache").GetRawText(), connectWritten.RootElement.GetProperty("PromptCache").GetRawText());
    }
}
