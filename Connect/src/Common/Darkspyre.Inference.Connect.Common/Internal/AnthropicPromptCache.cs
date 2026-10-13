using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Advisory cache markers for the shared Anthropic generation and counting payload.</summary>
internal static class AnthropicPromptCache
{
    internal static void ValidateOptions(LlmConnection connection, LlmChatRequest request)
    {
        if (request.PromptCache is not null) PromptCaching.RejectRawOption(connection, request, "cache_control");
    }

    internal static void Apply(JsonObject payload, LlmConnection connection, LlmChatRequest request)
    {
        if (request.PromptCache is not { } hint) return;
        ValidateOptions(connection, request);
        var marker = new JsonObject { ["type"] = "ephemeral" };
        if (hint.Retention == LlmPromptCacheRetention.Extended) marker["ttl"] = "1h";

        JsonObject? prefix = (payload["system"] as JsonArray)?.OfType<JsonObject>().LastOrDefault(block =>
            (string?)block["type"] == "text" && !string.IsNullOrEmpty((string?)block["text"]));
        prefix ??= (payload["tools"] as JsonArray)?.OfType<JsonObject>().LastOrDefault();
        if (prefix is not null) prefix["cache_control"] = marker.DeepClone();
        if (hint.Scope == LlmPromptCacheScope.Conversation) payload["cache_control"] = marker.DeepClone();
    }
}
