using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Providers;
using Xunit;

namespace Darkspyre.Inference.Connect.LLM.Tests;

/// <summary>The Claude CLI reports no context window, so its models take the published limits of the model each alias resolves to.</summary>
public sealed class ClaudeModelLimitsTests
{
    /// <summary>A model the connection's discovered catalog sizes takes that size, whether or not the published table lists it.</summary>
    [Fact]
    public void CliModelsTakeTheDiscoveredWindowOfTheirResolvedModel()
    {
        using JsonDocument initialization = JsonDocument.Parse("""
            {"models":[
              {"value":"haiku","resolvedModel":"claude-haiku-9","displayName":"Haiku 9"},
              {"value":"claude-haiku-4-5-20251001","resolvedModel":"claude-haiku-4-5-20251001","displayName":"Haiku 4.5"}]}
            """);
        LlmModel[] discovered =
        [
            new("claude-haiku-9", "Claude Haiku 9") { MaxContextTokens = 400_000, MaxOutputTokens = 32_000 },
            new("claude-haiku-4-5-20251001", "Claude Haiku 4.5") { MaxContextTokens = 250_000 }
        ];

        LlmModel[] models = [.. ClaudeCliModelCatalog.Project(initialization.RootElement, discovered)];

        Assert.Equal((400_000L, 32_000L), (models[0].MaxContextTokens, models[0].MaxOutputTokens));
        Assert.Equal((250_000L, 64_000L), (models[1].MaxContextTokens, models[1].MaxOutputTokens));
    }
}
