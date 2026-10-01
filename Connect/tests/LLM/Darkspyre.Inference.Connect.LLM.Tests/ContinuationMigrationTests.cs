using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ContinuationMigrationTests
{
    [Theory]
    [InlineData(LlmStrictInputSchemaSupport.Unknown)]
    [InlineData(LlmStrictInputSchemaSupport.Supported)]
    public void StrictAttemptsAreAllowedForUnknownAndSupported(LlmStrictInputSchemaSupport support)
    {
        var tool = new LlmToolDefinition
        {
            Name = "run",
            Strict = true,
            InputSchema = JsonElement.Parse("""{"type":"object","properties":{"optional":{"type":"string"}}}""")
        };
        LlmClientBase.ValidateToolUse(new() { ModelId = "model", Tools = [tool] },
            new() { SupportsToolUse = true, Tools = new() { StrictInputSchema = support } });
        Assert.True(tool.Strict);
        Assert.False(tool.InputSchema.TryGetProperty("required", out _));
    }

    [Fact]
    public void UnsupportedStrictnessDoesNotDisableOrdinaryTools()
    {
        var tool = new LlmToolDefinition { Name = "run", InputSchema = JsonElement.Parse("""{"type":"object"}""") };
        var capabilities = new LlmProviderCapabilities
        {
            SupportsToolUse = true,
            Tools = new() { StrictInputSchema = LlmStrictInputSchemaSupport.Unsupported }
        };
        LlmClientBase.ValidateToolUse(new() { ModelId = "model", Tools = [tool] }, capabilities);
        Assert.Throws<NotSupportedException>(() => LlmClientBase.ValidateToolUse(
            new() { ModelId = "model", Tools = [tool with { Strict = true }] }, capabilities));
        Assert.Equal(LlmStrictInputSchemaSupport.Unknown, new LlmToolCapabilities().StrictInputSchema);
    }

    [Theory]
    [InlineData("""{"Role":"assistant","Content":"retained","ResponseState":null}""")]
    [InlineData("""{"Role":"assistant","Content":"retained","ResponseState":{"FormatVersion":1}}""")]
    public void UnmigratedMessagesFailInsteadOfSilentlyDroppingState(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, ConnectLlmJson.ChatMessage));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, InferenceJson.LlmMessage));
    }

    [Fact]
    public void UnversionedCapturesRequireHostMigration()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"Entries":[]}""", ConnectLlmJson.AgentContext));
        Assert.Throws<ArgumentException>(() => ManagedAgentContext.Import(new() { FormatVersion = 1 }, new()
        { Version = "1", Transport = LlmTransport.OpenAiResponses, Endpoint = "https://example.invalid" }, "model"));
    }

    [Fact]
    public void PortableMessagesRetainOpaqueStateWithoutConnectTransportTypes()
    {
        var state = new LlmContinuationState
        { ImplementationId = "engine.owner", ModelId = "model", ScopeId = "scope", Payload = "private-state", InputTokens = 9, OutputTokens = 3 };
        var message = new LlmMessage("assistant", "answer") { ContinuationState = state };
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(message, InferenceJson.LlmMessage), InferenceJson.LlmMessage);
        Assert.Equal(state, copy!.ContinuationState);
        Assert.Equal(2, copy.ContinuationState!.FormatVersion);
        Assert.DoesNotContain("private-state", state.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10, 3, 5, 2, 17, 20)]
    [InlineData(10, 3, null, null, 10, 13)]
    [InlineData(null, 3, 5, null, null, null)]
    public void SplitInputCountersNormalizeOnce(int? input, int? output, int? cached, int? written, int? expectedInput, int? expectedTotal)
    {
        var usage = AnthropicUsage.Create(input, output, cached, written);
        Assert.Equal(expectedInput, usage!.InputTokens);
        Assert.Equal(expectedTotal, usage.TotalTokens);
        Assert.Equal(cached, usage.CachedInputTokens);
        Assert.Equal(written, usage.CacheWriteInputTokens);
    }
}
