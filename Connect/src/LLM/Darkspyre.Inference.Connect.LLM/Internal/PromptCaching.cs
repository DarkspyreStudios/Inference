using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Cache declarations shared by routes with the same implemented protocol.</summary>
internal static class PromptCaching
{
    public static LlmPromptCacheCapabilities Automatic { get; } = new() { Automatic = true };

    public static LlmPromptCacheCapabilities Claude { get; } = new()
    {
        AcceptsHints = true,
        Retentions = new[] { LlmPromptCacheRetention.Default, LlmPromptCacheRetention.Extended }.ToFrozenSet()
    };
}
