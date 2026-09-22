using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class RequestJson
{
    public static void ApplyProviderOptions(
        JsonObject payload,
        LlmConnection connection,
        LlmChatRequest request)
    {
        var options = new Dictionary<string, JsonElement>(connection.ProviderOptions, StringComparer.Ordinal);
        foreach ((string name, JsonElement value) in request.ProviderOptions)
        {
            options[name] = value;
        }

        foreach ((string name, JsonElement value) in options)
        {
            if (payload.ContainsKey(name))
            {
                throw new InvalidOperationException($"Provider option '{name}' cannot replace a standard request field.");
            }

            payload[name] = JsonNode.Parse(value.GetRawText());
        }
    }
}
