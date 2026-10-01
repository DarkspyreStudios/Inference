using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class RequestJson
{
    internal static void ValidateSamplingOptions(LlmConnection connection, LlmChatRequest request)
    {
        foreach (string name in connection.ProviderOptions.Keys.Concat(request.ProviderOptions.Keys))
        {
            if (IsSamplingOption(name))
                throw new InvalidOperationException($"Provider option '{name}' cannot replace a standard request field.");
        }
    }

    private static bool IsSamplingOption(string name) => name is "temperature" or "top_p" or "top_k" or "seed"
        or "presence_penalty" or "frequency_penalty" or "stop" or "stop_sequences"
        or "topP" or "topK" or "presencePenalty" or "frequencyPenalty" or "stopSequences";

    public static void ApplyProviderOptions(
        JsonObject payload,
        LlmConnection connection,
        LlmChatRequest request,
        string? consumedOption = null)
    {
        ValidateSamplingOptions(connection, request);
        var options = new Dictionary<string, JsonElement>(connection.ProviderOptions, StringComparer.Ordinal);
        foreach ((string name, JsonElement value) in request.ProviderOptions)
        {
            options[name] = value;
        }

        foreach ((string name, JsonElement value) in options)
        {
            if (name == consumedOption) continue;
            if (payload.ContainsKey(name))
            {
                throw new InvalidOperationException($"Provider option '{name}' cannot replace a standard request field.");
            }

            payload[name] = JsonNode.Parse(value.GetRawText());
        }
    }
}
