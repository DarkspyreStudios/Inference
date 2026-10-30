using Darkspyre.Inference.Common.LLM;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Which Responses output items a provider retains. A null field list keeps the item verbatim.</summary>
/// <param name="Items">The retained item types, each with the fields kept, or null to keep the item verbatim.</param>
internal sealed record ResponsesReplayRules(IReadOnlyDictionary<string, string[]?> Items)
{
    /// <summary>Extra checks on a retained item, after the shared required-field checks.</summary>
    public Action<JsonObject, string>? ValidateItem { get; init; }

    /// <summary>message, reasoning and function_call with the fields every projecting Responses provider keeps.</summary>
    public static IReadOnlyDictionary<string, string[]?> ProjectedBase { get; } = new Dictionary<string, string[]?>(StringComparer.Ordinal)
    {
        ["message"] = ["type", "id", "role", "status", "phase", "content"],
        ["reasoning"] = ["type", "id", "status", "summary", "content", "encrypted_content"],
        ["function_call"] = ["type", "id", "status", "call_id", "name", "arguments"]
    };
}

/// <summary>
/// Encodes and replays one Responses provider's continuation state by its replay rules. The state carries the transport's
/// implementation id, format version 2 and the account scope, as the shared rules have always written them.
/// </summary>
internal sealed class ResponsesContinuationCodec(ResponsesReplayRules rules) : ILlmContinuationCodec
{
    private const int MaxPayloadLength = 16 * 1024 * 1024;

    public LlmContinuationState Encode(LlmConnection connection, string modelId, JsonArray content) => new()
    {
        ImplementationId = ProviderContinuationStateCodec.ImplementationId(connection.Transport),
        ModelId = modelId,
        ScopeId = ProviderContinuationStateCodec.Scope(connection),
        Payload = Project(content, connection.Transport).ToJsonString()
    };

    public bool IsBound(LlmContinuationState state, LlmConnection connection, string modelId) =>
        state.FormatVersion == 2 && state.ImplementationId == ProviderContinuationStateCodec.ImplementationId(connection.Transport)
        && state.ModelId == modelId && state.ScopeId == ProviderContinuationStateCodec.Scope(connection);

    public JsonArray DecodeMessage(LlmChatMessage message, LlmConnection connection, string modelId)
    {
        ProviderContinuationStateCodec.ValidateMessage(message);
        LlmContinuationState state = message.ContinuationState!;
        if (state.Payload is null || state.Payload.Length > MaxPayloadLength || !IsBound(state, connection, modelId))
        {
            throw new ArgumentException("Response state does not match this version, transport, model, or account scope.");
        }
        JsonArray output;
        try
        {
            output = JsonNode.Parse(state.Payload) as JsonArray
                ?? throw new ArgumentException("Response state must contain ordered assistant output.");
            if (!JsonNode.DeepEquals(output, Project(output, connection.Transport)))
            {
                throw new ArgumentException("Response state contains unsupported fields.");
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Do not include a parser exception that might expose sensitive payload text.
            throw new ArgumentException("Response state contains invalid assistant output.");
        }
        ProviderContinuationStateCodec.MatchMessage(message, output, connection.Transport);
        return output;
    }

    private JsonArray Project(JsonArray output, LlmTransport transport)
    {
        var projected = new JsonArray();
        foreach (JsonNode? node in output)
        {
            if (node is not JsonObject item || item["type"]?.GetValue<string>() is not { } type)
            {
                throw new ArgumentException("Response state contains an invalid output item.");
            }
            if (!rules.Items.TryGetValue(type, out string[]? fields))
            {
                throw new NotSupportedException("The adapter cannot retain this assistant output type.");
            }
            JsonObject copy;
            if (fields is null)
            {
                // A verbatim item is opaque replay data: the library never runs or logs its fields.
                if (type == "message" && (item["content"] is not JsonArray content || content.Any(part => part is not JsonObject)))
                {
                    throw new ArgumentException("Response state content must contain ordered assistant blocks.");
                }
                copy = (JsonObject)item.DeepClone();
            }
            else
            {
                copy = ProviderContinuationStateCodec.ProjectFields(item, fields, transport);
            }
            if (type == "message" && copy["role"]?.GetValue<string>() != "assistant")
            {
                throw new ArgumentException("Response state cannot contain non-assistant messages.");
            }
            ProviderContinuationStateCodec.ValidateItem(copy, transport, type);
            rules.ValidateItem?.Invoke(copy, type);
            projected.Add(item: copy);
        }
        return projected;
    }
}
