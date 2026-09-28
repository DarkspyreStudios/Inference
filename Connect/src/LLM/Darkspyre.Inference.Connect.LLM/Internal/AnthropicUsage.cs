using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class AnthropicUsage
{
    // Anthropic reports uncached input separately from cache reads and creation.
    public static LlmUsage? Create(int? uncached, int? output, int? cached, int? written)
    {
        int? input = uncached is null ? null : checked(uncached + (cached ?? 0) + (written ?? 0));
        return input is null && output is null && cached is null && written is null ? null
            : new(input, output, input is not null && output is not null ? checked(input + output) : null)
            {
                CachedInputTokens = cached,
                CacheWriteInputTokens = written
            };
    }
}
