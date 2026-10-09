using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Providers;
using Xunit;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>The Claude CLI reports no context window, so its models take the published limits of the model each alias resolves to.</summary>
public sealed class ClaudeModelLimitsTests
{
    [Fact]
    public void CliModelsTakeThePublishedWindowOfTheirResolvedModel()
    {
        using JsonDocument initialization = JsonDocument.Parse("""
            {"models":[
              {"value":"opus","resolvedModel":"claude-opus-5-5","displayName":"Opus 5.5"},
              {"value":"claude-haiku-4-5-20251001","resolvedModel":"claude-haiku-4-5-20251001","displayName":"Haiku 4.5"},
              {"value":"sonnet[1m]","displayName":"Sonnet (1M)"},
              {"value":"unlisted","displayName":"Unlisted"}]}
            """);

        LlmModel[] models = [.. ClaudeCliModelCatalog.Project(initialization.RootElement)];

        Assert.Equal((1_000_000L, 128_000L), (models[0].MaxContextTokens, models[0].MaxOutputTokens));
        Assert.Equal((200_000L, 64_000L), (models[1].MaxContextTokens, models[1].MaxOutputTokens));
        Assert.Equal(1_000_000L, models[2].MaxContextTokens);
        Assert.Null(models[3].MaxContextTokens);
    }
}
