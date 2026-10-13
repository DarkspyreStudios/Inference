using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Allowlisted Messages continuation metadata. Container IDs stay opaque and account-bound with content.</summary>
internal static class AnthropicContinuation
{
    internal static string EncodePayload(JsonArray content) => new JsonObject { ["content"] = content }.ToJsonString();

    internal static (JsonArray Content, string? ContainerId) ReadPayload(string payload)
    {
        if (JsonNode.Parse(payload) is not JsonObject envelope || envelope["content"] is not JsonArray content
            || envelope.Any(field => field.Key is not ("content" or "container_id")))
            throw new ArgumentException("Invalid Anthropic continuation envelope.");
        string? containerId = null;
        if (envelope.ContainsKey("container_id"))
        {
            if (envelope["container_id"] is not JsonValue value || !value.TryGetValue(out containerId)
                || string.IsNullOrWhiteSpace(containerId))
                throw new ArgumentException("Invalid Anthropic continuation container identity.");
        }
        return (content, containerId);
    }

    internal static LlmContinuationState WithContainer(LlmContinuationState state, string? containerId)
    {
        if (containerId is null) return state;
        if (string.IsNullOrWhiteSpace(containerId)) throw new ArgumentException("Invalid Anthropic container identity.");
        var (content, _) = ReadPayload(state.Payload);
        JsonObject envelope = content.Parent!.AsObject();
        envelope["container_id"] = containerId;
        return state with { Payload = envelope.ToJsonString() };
    }
}
