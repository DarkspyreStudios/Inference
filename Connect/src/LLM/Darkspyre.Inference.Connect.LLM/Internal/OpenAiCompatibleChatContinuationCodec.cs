using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Account/model-bound assistant state containing only replayable chat fields.</summary>
internal sealed class OpenAiCompatibleChatContinuationCodec : ILlmContinuationCodec
{
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;

    public bool IsBound(LlmContinuationState state, LlmConnection connection, string modelId) =>
        state.FormatVersion == 2 && state.ImplementationId == ImplementationId(connection.Transport) && state.ModelId == modelId
        && connection.ResolvedContinuationScope is { } scope && state.ScopeId == scope;

    public LlmContinuationState Encode(LlmConnection connection, string modelId, JsonArray content)
    {
        string payload = Validate(content).ToJsonString();
        if (Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes) { throw Invalid(); }
        return new()
        {
            ImplementationId = ImplementationId(connection.Transport),
            ModelId = modelId,
            ScopeId = connection.ResolvedContinuationScope ?? throw new InvalidOperationException("Chat continuation identity is unresolved."),
            Payload = payload
        };
    }

    public JsonArray DecodeMessage(LlmChatMessage message, LlmConnection connection, string modelId)
    {
        ProviderContinuationStateCodec.ValidateMessage(message);
        var state = message.ContinuationState ?? throw Invalid();
        if (!IsBound(state, connection, modelId) || state.Payload is null || state.Payload.Length > MaximumPayloadBytes
            || Encoding.UTF8.GetByteCount(state.Payload) > MaximumPayloadBytes) { throw Invalid(); }
        try
        {
            JsonArray content = Validate(JsonNode.Parse(state.Payload) as JsonArray ?? throw Invalid());
            var item = (JsonObject)content[0]!;
            if ((string?)item["text"] != message.Content) { throw Invalid(); }
            var calls = (JsonArray)item["tool_calls"]!;
            if (calls.Count != message.ToolCalls.Count) { throw Invalid(); }
            for (int index = 0; index < calls.Count; index++)
            {
                var call = (JsonObject)calls[index]!;
                var expected = ClientTools.Functions(message.ToolCalls)[index];
                if ((string?)call["id"] != expected.Id || (string?)call["name"] != expected.Name
                    || !JsonNode.DeepEquals(JsonNode.Parse((string)call["arguments"]!), JsonNode.Parse(expected.ArgumentsJson)))
                { throw Invalid(); }
            }
            return content;
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or FormatException)
        { throw Invalid(); }
    }

    private static JsonArray Validate(JsonArray content)
    {
        try
        {
            if (content.Count != 1 || content[0] is not JsonObject item
                || item.Any(field => field.Key is not ("type" or "text" or "tool_calls" or "reasoning_content" or "reasoning_details"))
                || (string?)item["type"] != "chat_message" || item["text"] is null || item["tool_calls"] is not JsonArray calls)
            { throw Invalid(); }
            _ = item["text"]!.GetValue<string>();
            _ = item["reasoning_content"]?.GetValue<string>();
            foreach (JsonNode? node in calls)
            {
                if (node is not JsonObject call || call.Count != 3 || call.Any(field => field.Key is not ("id" or "name" or "arguments"))
                    || string.IsNullOrWhiteSpace((string?)call["id"]) || string.IsNullOrWhiteSpace((string?)call["name"])
                    || JsonNode.Parse((string?)call["arguments"] ?? string.Empty) is not JsonObject) { throw Invalid(); }
            }
            if (item["reasoning_details"] is { } details)
            {
                if (details is not JsonArray array) { throw Invalid(); }
                foreach (JsonNode? node in array)
                {
                    if (node is not JsonObject detail) { throw Invalid(); }
                    ChatReasoning.ValidateDetail(detail);
                }
            }
            return (JsonArray)content.DeepClone();
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or FormatException)
        { throw Invalid(); }
    }

    private static string ImplementationId(LlmTransport transport) => ProviderContinuationStateCodec.ImplementationId(transport)
        + (transport == LlmTransport.XaiResponses ? ".chat-completions" : "");

    private static ArgumentException Invalid() => new("Invalid OpenAI-compatible assistant continuation content.");
}
