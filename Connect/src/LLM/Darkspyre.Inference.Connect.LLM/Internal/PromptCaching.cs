using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Cache declarations shared by routes with the same implemented protocol.</summary>
internal static class PromptCaching
{
    public static LlmPromptCacheCapabilities Automatic { get; } = new() { Automatic = InferenceSupport.Supported };

    public static LlmPromptCacheCapabilities Claude { get; } = new()
    {
        AcceptsHints = InferenceSupport.Supported,
        Retentions = new[] { LlmPromptCacheRetention.Default, LlmPromptCacheRetention.Extended }.ToFrozenSet()
    };

    public static LlmPromptCacheCapabilities OpenAi { get; } = Claude with { Automatic = InferenceSupport.Supported };

    internal static void RejectRawOption(LlmConnection connection, LlmChatRequest request, string name)
    {
        if (connection.ProviderOptions.Keys.Concat(request.ProviderOptions.Keys).Contains(name, StringComparer.Ordinal))
            throw new InvalidOperationException($"Provider option '{name}' cannot replace a standard request field.");
    }
}
