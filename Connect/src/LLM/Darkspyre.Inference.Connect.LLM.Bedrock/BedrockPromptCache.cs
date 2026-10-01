using System.Collections.Frozen;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

/// <summary>Claude cache points on the canonical Converse request shared by both response modes.</summary>
internal static class BedrockPromptCache
{
    private static readonly LlmPromptCacheCapabilities DefaultRetention = new()
    {
        AcceptsHints = true,
        Retentions = new[] { LlmPromptCacheRetention.Default }.ToFrozenSet()
    };

    public static LlmPromptCacheCapabilities Capabilities(string modelId)
    {
        (BedrockModelFamily family, string model) = BedrockModelFamilies.Classify(modelId);
        if (family != BedrockModelFamily.AnthropicClaude) { return new(); }
        return model.StartsWith("anthropic.claude-3-7-sonnet", StringComparison.Ordinal)
            || model.StartsWith("anthropic.claude-3-5-sonnet", StringComparison.Ordinal)
                ? DefaultRetention : PromptCaching.Claude;
    }

    public static void Apply(ConverseRequest result, LlmPromptCacheHint? hint, string modelId)
    {
        if (hint is null) { return; }
        LlmPromptCacheCapabilities capabilities = Capabilities(modelId);
        if (!capabilities.AcceptsHints) { return; }
        var point = new CachePointBlock
        {
            Type = CachePointType.Default,
            Ttl = hint.Retention == LlmPromptCacheRetention.Extended
                && capabilities.Retentions.Contains(LlmPromptCacheRetention.Extended)
                    ? CacheTTL.ONE_HOUR : CacheTTL.FIVE_MINUTES
        };
        if (result.System is { Count: > 0 })
        {
            result.System.Add(new SystemContentBlock { CachePoint = point });
        }
        else if (result.ToolConfig?.Tools is { Count: > 0 } tools)
        {
            tools.Add(new Tool { CachePoint = point });
        }
        if (hint.Scope == LlmPromptCacheScope.Conversation)
        {
            result.Messages[^1].Content.Add(new ContentBlock { CachePoint = point });
        }
    }
}
