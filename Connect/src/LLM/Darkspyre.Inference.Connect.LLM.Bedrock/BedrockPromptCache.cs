using System.Collections.Frozen;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

/// <summary>Documented model facts and cache points on the shared Converse request.</summary>
internal static class BedrockPromptCache
{
    private static readonly LlmPromptCacheCapabilities DefaultRetention = new()
    {
        Automatic = true,
        AcceptsHints = true,
        Retentions = new[] { LlmPromptCacheRetention.Default }.ToFrozenSet()
    };

    private static readonly LlmPromptCacheCapabilities ExtendedRetention = PromptCaching.Claude with { Automatic = true };

    // https://docs.aws.amazon.com/bedrock/latest/userguide/prompt-caching.html
    private static LlmPromptCacheCapabilities Facts(string model) => model switch
    {
        "anthropic.claude-sonnet-5-5" or "anthropic.claude-opus-5-5"
        or "anthropic.claude-fable-5-1" or "anthropic.claude-mythos-5-1"
        or "anthropic.claude-fable-5" or "anthropic.claude-mythos-5"
        or "anthropic.claude-opus-5" or "anthropic.claude-opus-4-8"
        or "anthropic.claude-opus-4-7" or "anthropic.claude-opus-4-6-v1"
        or "anthropic.claude-opus-4-5-20251101-v1:0" or "anthropic.claude-sonnet-5"
        or "anthropic.claude-sonnet-4-6" or "anthropic.claude-sonnet-4-5-20250929-v1:0"
        or "anthropic.claude-haiku-4-5-20251001-v1:0" => ExtendedRetention,
        "anthropic.claude-3-7-sonnet-20250219-v1:0"
        or "anthropic.claude-3-5-sonnet-20241022-v2:0" => DefaultRetention,
        // The corresponding AWS model cards specify system/messages only and five-minute TTL.
        "amazon.nova-micro-v1:0" or "amazon.nova-lite-v1:0" or "amazon.nova-pro-v1:0"
        or "amazon.nova-2-lite-v1:0" => DefaultRetention,
        _ => new()
    };

    public static LlmPromptCacheCapabilities Capabilities(string modelId) =>
        Facts(BedrockModelFamilies.Classify(modelId).BaseModelId);

    public static void Apply(ConverseRequest result, LlmPromptCacheHint? hint, string modelId)
    {
        if (hint is null) return;
        string model = BedrockModelFamilies.Classify(modelId).BaseModelId;
        LlmPromptCacheCapabilities capabilities = Facts(model);
        if (!capabilities.AcceptsHints) return;
        var point = new CachePointBlock
        {
            Type = CachePointType.Default,
            Ttl = hint.Retention == LlmPromptCacheRetention.Extended
                && capabilities.Retentions.Contains(LlmPromptCacheRetention.Extended)
                    ? CacheTTL.ONE_HOUR : CacheTTL.FIVE_MINUTES
        };
        if (result.System is { Count: > 0 })
            result.System.Add(new SystemContentBlock { CachePoint = point });
        else if (model.StartsWith("anthropic.", StringComparison.Ordinal) && result.ToolConfig?.Tools is { Count: > 0 } tools)
            tools.Add(new Tool { CachePoint = point });
        if (hint.Scope == LlmPromptCacheScope.Conversation && result.Messages.Count > 0)
            result.Messages[^1].Content.Add(new ContentBlock { CachePoint = point });
    }
}
