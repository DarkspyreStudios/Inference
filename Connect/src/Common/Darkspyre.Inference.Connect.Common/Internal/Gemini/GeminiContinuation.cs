using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Validates retained Gemini parts. Thought signatures stay on the part the provider returned them on.</summary>
internal static class GeminiContinuation
{
    public const int MaxPayloadLength = 16 * 1024 * 1024;

    /// <summary>Projects omitted arguments as an empty object without changing the retained native call.</summary>
    internal static JsonObject? EffectiveArguments(JsonObject call) =>
        call.TryGetPropertyValue("args", out JsonNode? arguments) ? arguments as JsonObject : new JsonObject();

    public static JsonArray WireParts(JsonArray parts)
    {
        var wire = (JsonArray)parts.DeepClone();
        foreach (JsonObject part in wire.OfType<JsonObject>()) { part.Remove("connectCallId"); }
        return wire;
    }

    public static JsonArray Validate(JsonArray content)
    {
        foreach (JsonNode? node in content)
        {
            if (node is not JsonObject part || part.Any(field => field.Key is not ("text" or "thought" or "thoughtSignature" or "functionCall" or "connectCallId")))
            { throw new ArgumentException("Unsupported Gemini continuation part."); }
            _ = part["text"]?.GetValue<string>();
            _ = part["thought"]?.GetValue<bool>();
            _ = part["thoughtSignature"]?.GetValue<string>();
            if (part["functionCall"] is JsonObject call)
            {
                if (call.Any(field => field.Key is not ("id" or "name" or "args"))
                    || string.IsNullOrWhiteSpace((string?)call["name"]) || EffectiveArguments(call) is null
                    || string.IsNullOrWhiteSpace((string?)part["connectCallId"])) { throw new ArgumentException("Invalid Gemini function call."); }
                _ = call["id"]?.GetValue<string>();
            }
            else if (part["functionCall"] is not null || part["connectCallId"] is not null) { throw new ArgumentException("Continuation content does not match its visible message."); }
        }
        return (JsonArray)content.DeepClone();
    }

    /// <summary>Parses a retained payload and requires it to match the visible text and tool calls it replays.</summary>
    public static JsonArray DecodeVisible(string payload, LlmChatMessage message)
    {
        JsonArray parts = Validate(JsonNode.Parse(payload)!.AsArray());
        string text = string.Concat(parts.OfType<JsonObject>().Where(part => (bool?)part["thought"] != true).Select(part => (string?)part["text"]));
        if (text != message.Content) { throw new ArgumentException("Continuation content does not match its visible message."); }
        JsonObject[] calls = parts.OfType<JsonObject>().Where(part => part["functionCall"] is not null).ToArray();
        if (calls.Length != message.ToolCalls.Count) { throw new ArgumentException("Continuation content does not match its visible message."); }
        for (int index = 0; index < calls.Length; index++)
        {
            JsonObject call = calls[index]["functionCall"]!.AsObject();
            var expected = ClientTools.Functions(message.ToolCalls)[index];
            if ((string?)calls[index]["connectCallId"] != expected.Id || (string?)call["name"] != expected.Name
                || !JsonNode.DeepEquals(EffectiveArguments(call), JsonNode.Parse(expected.ArgumentsJson))) { throw new ArgumentException("Continuation content does not match its visible message."); }
        }
        return parts;
    }
}

/// <summary>Continuation codec for a route that carries only Gemini parts, bound to the connection's resolved scope.</summary>
internal sealed class GeminiContinuationCodec(string owner, string routeName) : ILlmContinuationCodec
{
    public bool IsBound(LlmContinuationState state, LlmConnection connection, string modelId) =>
        state.FormatVersion == 2 && state.ImplementationId == owner && state.ModelId == modelId
        && connection.ResolvedContinuationScope is { } scope && state.ScopeId == scope;

    public LlmContinuationState Encode(LlmConnection connection, string modelId, JsonArray content) => new()
    {
        ImplementationId = owner,
        ModelId = modelId,
        ScopeId = connection.ResolvedContinuationScope ?? throw new InvalidOperationException($"{routeName} identity is unresolved."),
        Payload = GeminiContinuation.Validate(content).ToJsonString()
    };

    public JsonArray DecodeMessage(LlmChatMessage message, LlmConnection connection, string modelId)
    {
        var state = message.ContinuationState ?? throw new ArgumentException($"Missing {routeName} continuation state.");
        if (!IsBound(state, connection, modelId) || state.Payload is null || state.Payload.Length > GeminiContinuation.MaxPayloadLength)
        {
            throw new ArgumentException($"{routeName} continuation state has an incompatible binding or size.");
        }
        try { return GeminiContinuation.DecodeVisible(state.Payload, message); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ArgumentException($"Invalid {routeName} assistant continuation content.");
        }
    }
}
