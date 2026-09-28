using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal sealed class VertexContinuationCodec : ILlmContinuationCodec
{
    private const string Owner = "darkspyre.connect.llm.google-vertex-ai";

    public bool IsBound(LlmContinuationState state, LlmConnection connection, string modelId) =>
        state.FormatVersion == 2 && state.ImplementationId == Owner && state.ModelId == modelId
        && connection.ResolvedContinuationScope is { } scope && state.ScopeId == scope;

    public LlmContinuationState Encode(LlmConnection connection, string modelId, JsonArray content)
    {
        JsonArray projected = VertexEndpoints.ParseModel(modelId).Publisher == "anthropic"
            ? JsonNode.Parse(ProviderContinuationStateCodec.Encode(AnthropicConnection(connection), modelId, content).Payload)!.AsArray()
            : ValidateGemini(content);
        return new()
        {
            ImplementationId = Owner,
            ModelId = modelId,
            ScopeId = connection.ResolvedContinuationScope ?? throw new InvalidOperationException("Vertex identity is unresolved."),
            Payload = projected.ToJsonString()
        };
    }

    public JsonArray DecodeMessage(LlmChatMessage message, LlmConnection connection, string modelId)
    {
        var state = message.ContinuationState ?? throw new ArgumentException("Missing Vertex continuation state.");
        if (!IsBound(state, connection, modelId) || state.Payload is null || state.Payload.Length > 16 * 1024 * 1024)
        {
            throw new ArgumentException("Vertex continuation state has an incompatible binding or size.");
        }
        try
        {
            if (VertexEndpoints.ParseModel(modelId).Publisher == "anthropic")
            {
                LlmConnection native = AnthropicConnection(connection);
                var nativeState = state with
                {
                    ImplementationId = ProviderContinuationStateCodec.ImplementationId(LlmTransport.AnthropicMessages),
                    ScopeId = ProviderContinuationStateCodec.Scope(native)
                };
                return ProviderContinuationStateCodec.DecodeMessage(message with { ContinuationState = nativeState }, native, modelId);
            }

            JsonArray parts = ValidateGemini(JsonNode.Parse(state.Payload)!.AsArray());
            string text = string.Concat(parts.OfType<JsonObject>().Where(part => (bool?)part["thought"] != true).Select(part => (string?)part["text"]));
            if (text != message.Content) { throw new ArgumentException("Continuation content does not match its visible message."); }
            JsonObject[] calls = parts.OfType<JsonObject>().Where(part => part["functionCall"] is not null).ToArray();
            if (calls.Length != message.ToolCalls.Count) { throw new ArgumentException("Continuation content does not match its visible message."); }
            for (int index = 0; index < calls.Length; index++)
            {
                var call = calls[index]["functionCall"]!;
                var expected = message.ToolCalls[index];
                if ((string?)calls[index]["connectCallId"] != expected.Id || (string?)call["name"] != expected.Name
                    || !JsonNode.DeepEquals(call["args"], JsonNode.Parse(expected.ArgumentsJson))) { throw new ArgumentException("Continuation content does not match its visible message."); }
            }
            return parts;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ArgumentException("Invalid Vertex assistant continuation content.");
        }
    }

    internal static JsonArray WireParts(JsonArray parts)
    {
        var wire = (JsonArray)parts.DeepClone();
        foreach (JsonObject part in wire.OfType<JsonObject>()) { part.Remove("connectCallId"); }
        return wire;
    }

    private static JsonArray ValidateGemini(JsonArray content)
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
                    || string.IsNullOrWhiteSpace((string?)call["name"]) || call["args"] is not JsonObject
                    || string.IsNullOrWhiteSpace((string?)part["connectCallId"])) { throw new ArgumentException("Invalid Gemini function call."); }
                _ = call["id"]?.GetValue<string>();
            }
            else if (part["functionCall"] is not null || part["connectCallId"] is not null) { throw new ArgumentException("Continuation content does not match its visible message."); }
        }
        return (JsonArray)content.DeepClone();
    }

    private static LlmConnection AnthropicConnection(LlmConnection connection) => connection with
    {
        Transport = LlmTransport.AnthropicMessages,
        ContinuationCodec = null,
        ResolvedContinuationScope = null
    };
}
