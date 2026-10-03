using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Documented cache controls on the recognized OpenRouter Chat Completions route.</summary>
internal static class OpenRouterPromptCache
{
    private static readonly LlmPromptCacheCapabilities DefaultRetention = new()
    {
        AcceptsHints = true,
        Retentions = new[] { LlmPromptCacheRetention.Default }.ToFrozenSet()
    };

    // https://openrouter.ai/docs/guides/best-practices/prompt-caching
    internal static LlmPromptCacheCapabilities Capabilities(string? model)
    {
        if (model is null) return new();
        if (model.StartsWith("anthropic/claude-", StringComparison.Ordinal)) return PromptCaching.Claude;
        if (model.StartsWith("openai/", StringComparison.Ordinal))
            return OpenAiModelProfiles.Get(model[7..]).SupportsPromptCacheOptions ? PromptCaching.OpenAi : PromptCaching.Automatic;
        if (model.StartsWith("google/gemini-2.5", StringComparison.Ordinal)
            || model.StartsWith("google/gemini-3", StringComparison.Ordinal))
            return DefaultRetention with { Automatic = true };
        if (model is "deepseek/deepseek-v3.2" or "qwen/qwen3-max" or "qwen/qwen-plus"
            or "qwen/qwen3.6-plus" or "qwen/qwen3-coder-plus" or "qwen/qwen3-coder-flash")
            return DefaultRetention;
        if (model.StartsWith("x-ai/", StringComparison.Ordinal) || model.StartsWith("moonshotai/", StringComparison.Ordinal)
            || model.StartsWith("deepseek/", StringComparison.Ordinal) || model.StartsWith("z-ai/", StringComparison.Ordinal))
            return PromptCaching.Automatic;
        return new();
    }

    internal static void Apply(JsonObject payload, LlmConnection connection, LlmChatRequest request)
    {
        if (request.PromptCache is not { } hint || !Capabilities(request.ModelId).AcceptsHints) return;
        bool openAi = request.ModelId.StartsWith("openai/", StringComparison.Ordinal);
        bool claude = request.ModelId.StartsWith("anthropic/", StringComparison.Ordinal);
        PromptCaching.RejectRawOption(connection, request, openAi ? "prompt_cache_options" : "cache_control");
        JsonArray messages = (JsonArray)payload["messages"]!;
        if (claude && hint.Scope == LlmPromptCacheScope.Conversation)
        {
            payload["cache_control"] = Marker(hint, extended: true);
            return;
        }

        IEnumerable<JsonObject> candidates = messages.OfType<JsonObject>();
        if (hint.Scope == LlmPromptCacheScope.Prefix)
            candidates = candidates.Where(message => (string?)message["role"] is "system" or "developer");
        JsonObject? block = candidates.Reverse().Select(LastText).FirstOrDefault(value => value is not null);
        // Tool-only Prefix has no verified OpenRouter tool marker. Do not change its prompt or tools.
        if (block is null) return;
        if (openAi)
        {
            block["prompt_cache_breakpoint"] = new JsonObject { ["mode"] = "explicit" };
            if (hint.Retention == LlmPromptCacheRetention.Extended)
                payload["prompt_cache_options"] = new JsonObject { ["ttl"] = "30m" };
        }
        else block["cache_control"] = Marker(hint, extended: claude);
    }

    private static JsonObject Marker(LlmPromptCacheHint hint, bool extended)
    {
        var marker = new JsonObject { ["type"] = "ephemeral" };
        if (extended && hint.Retention == LlmPromptCacheRetention.Extended) marker["ttl"] = "1h";
        return marker;
    }

    private static JsonObject? LastText(JsonObject message)
    {
        if (message["content"] is JsonArray parts)
            return parts.OfType<JsonObject>().LastOrDefault(part => (string?)part["type"] == "text"
                && !string.IsNullOrEmpty((string?)part["text"]));
        if (message["content"] is not JsonValue value || !value.TryGetValue(out string? text) || string.IsNullOrEmpty(text))
            return null;
        var block = new JsonObject { ["type"] = "text", ["text"] = text };
        message["content"] = new JsonArray(block);
        return block;
    }
}
