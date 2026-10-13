using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class ClaudeCliControlProtocol
{
    internal static JsonObject Request(string requestId, JsonNode request) => new()
    {
        ["type"] = "control_request",
        ["request_id"] = requestId,
        ["request"] = request
    };

    internal static bool TryReadResponse(JsonElement root, out string requestId, out JsonElement response)
    {
        requestId = string.Empty;
        response = default;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("response", out response)
            || response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("request_id", out JsonElement identity)
            || identity.ValueKind != JsonValueKind.String) return false;
        requestId = identity.GetString()!;
        return true;
    }

    internal static bool IsRejected(JsonElement response) => response.TryGetProperty("subtype", out JsonElement subtype)
        && subtype.ValueKind == JsonValueKind.String && subtype.GetString() == "error";

    internal static JsonElement Payload(JsonElement response) => response.TryGetProperty("response", out JsonElement payload)
        ? payload.Clone() : JsonElement.Parse("{}");
}
