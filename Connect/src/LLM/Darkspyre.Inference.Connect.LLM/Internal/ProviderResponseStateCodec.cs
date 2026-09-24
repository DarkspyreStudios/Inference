using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Versioned, account-bound output only. Never stores an unfiltered provider response.</summary>
internal static class ProviderResponseStateCodec
{
    public static LlmProviderResponseState Encode(
        LlmConnection connection,
        string modelId,
        JsonArray output) => new()
        {
            Transport = connection.Transport,
            ModelId = modelId,
            ScopeId = Scope(connection),
            Payload = Project(output, connection.Transport).ToJsonString()
        };

    /// <summary>Transports whose adapters replay response state.</summary>
    public static bool Retains(LlmTransport transport) =>
        transport is LlmTransport.OpenAiResponses or LlmTransport.OpenAiCodex or LlmTransport.AnthropicMessages;

    /// <summary>Whether this state can be replayed on the connection and model.</summary>
    public static bool IsBound(LlmProviderResponseState state, LlmConnection connection, string modelId) =>
        Retains(connection.Transport) && state.FormatVersion == 1 && state.Transport == connection.Transport
        && state.ModelId == modelId && state.ScopeId == Scope(connection);

    public static JsonArray Decode(
        LlmProviderResponseState state,
        LlmConnection connection,
        string modelId)
    {
        if (!IsBound(state, connection, modelId))
        {
            throw new ArgumentException("Response state does not match this version, transport, model, or account scope.");
        }

        try
        {
            if (JsonNode.Parse(state.Payload) is not JsonArray output)
            {
                throw new ArgumentException("Response state must contain ordered assistant output.");
            }

            JsonArray projected = Project(output, connection.Transport);
            if (!JsonNode.DeepEquals(output, projected))
            {
                throw new ArgumentException("Response state contains unsupported fields.");
            }

            return projected;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // Do not include a parser exception that might expose sensitive payload text.
            throw new ArgumentException("Response state contains invalid assistant output.");
        }
    }

    public static void ValidateMessage(LlmChatMessage message)
    {
        if (message.Role != "assistant" || message.ToolResults.Count != 0 || message.Attachments.Count != 0)
        {
            throw new ArgumentException("Response state belongs only to an assistant message without input attachments or tool results.");
        }
    }

    public static JsonArray DecodeMessage(LlmChatMessage message, LlmConnection connection, string modelId)
    {
        ValidateMessage(message);
        JsonArray output = Decode(message.ResponseState!, connection, modelId);
        var calls = output.OfType<JsonObject>()
            .Where(item => item["type"]?.GetValue<string>() is "function_call" or "tool_use").ToArray();
        if (calls.Length != message.ToolCalls.Count)
        {
            throw new ArgumentException("Assistant tool calls must match its opaque response state.");
        }
        for (int index = 0; index < calls.Length; index++)
        {
            JsonObject call = calls[index];
            bool anthropic = call["type"]?.GetValue<string>() == "tool_use";
            var visible = message.ToolCalls[index];
            try
            {
                if (call[anthropic ? "id" : "call_id"]?.GetValue<string>() != visible.Id
                    || call["name"]?.GetValue<string>() != visible.Name
                    || !JsonNode.DeepEquals(anthropic ? call["input"]
                            : JsonNode.Parse(call["arguments"]!.GetValue<string>()),
                        JsonNode.Parse(visible.ArgumentsJson)))
                {
                    throw new ArgumentException("Assistant tool calls must match its opaque response state.");
                }
            }
            catch (JsonException)
            {
                throw new ArgumentException("Assistant tool arguments contain invalid JSON.");
            }
        }

        return output;
    }

    internal static string Scope(LlmConnection connection)
    {
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        // The concurrency Version and rotating OAuth tokens are not account identities. Missing an
        // OAuth account ID binds conservatively to its current token; never guess account equivalence.
        string identity = JsonSerializer.Serialize(new
        {
            connection.Transport,
            connection.Endpoint,
            credential?.ApiKey,
            OAuthIdentity = credential?.OAuth?.AccountId ?? credential?.OAuth?.AccessToken,
            Headers = connection.AdditionalHeaders.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new { Name = pair.Key.ToLowerInvariant(), pair.Value }),
            SecretHeaders = credential?.SecretHeaders.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new { Name = pair.Key.ToLowerInvariant(), pair.Value })
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static JsonArray Project(JsonArray output, LlmTransport transport)
    {
        var projected = new JsonArray();
        foreach (JsonNode? node in output)
        {
            if (node is not JsonObject item || item["type"]?.GetValue<string>() is not { } type)
            {
                throw new ArgumentException("Response state contains an invalid output item.");
            }

            string[] fields = (transport, type) switch
            {
                (LlmTransport.OpenAiResponses or LlmTransport.OpenAiCodex, "message") =>
                    ["type", "id", "role", "status", "phase", "content"],
                (LlmTransport.OpenAiResponses or LlmTransport.OpenAiCodex, "reasoning") =>
                    ["type", "id", "status", "summary", "content", "encrypted_content"],
                (LlmTransport.OpenAiResponses or LlmTransport.OpenAiCodex, "function_call") =>
                    ["type", "id", "status", "call_id", "name", "arguments"],
                (LlmTransport.AnthropicMessages, "text") => ["type", "text"],
                (LlmTransport.AnthropicMessages, "thinking") => ["type", "thinking", "signature"],
                (LlmTransport.AnthropicMessages, "redacted_thinking") => ["type", "data"],
                (LlmTransport.AnthropicMessages, "tool_use") => ["type", "id", "name", "input"],
                _ => throw new NotSupportedException("The adapter cannot retain this assistant output type.")
            };
            var copy = new JsonObject();
            foreach (string field in fields)
            {
                if (item.TryGetPropertyValue(field, out JsonNode? value))
                {
                    if (field is "content" or "summary")
                    {
                        copy[field] = ProjectTextBlocks(value);
                    }
                    else if (field == "input")
                    {
                        if (value is not JsonObject)
                        {
                            throw new ArgumentException("Tool input in response state must be a JSON object.");
                        }
                        copy[field] = value.DeepClone();
                    }
                    else
                    {
                        _ = value?.GetValue<string>();
                        copy[field] = value?.DeepClone();
                    }
                }
            }

            if (type == "message" && copy["role"]?.GetValue<string>() != "assistant")
            {
                throw new ArgumentException("Response state cannot contain non-assistant messages.");
            }
            ValidateItem(copy, type);
            projected.Add(copy);
        }

        return projected;
    }

    private static void ValidateItem(JsonObject item, string type)
    {
        string[] required = type switch
        {
            "message" => ["content"],
            "function_call" => ["call_id", "name", "arguments"],
            "tool_use" => ["id", "name", "input"],
            "thinking" => ["thinking", "signature"],
            "redacted_thinking" => ["data"],
            "text" => ["text"],
            _ => []
        };
        if (required.Any(field => item[field] is null)
            || (type is "function_call" or "tool_use"
                && (string.IsNullOrWhiteSpace(item[type == "tool_use" ? "id" : "call_id"]!.GetValue<string>())
                    || string.IsNullOrWhiteSpace(item["name"]!.GetValue<string>()))))
        {
            throw new ArgumentException("Response state is missing required assistant output fields.");
        }
        if (type == "function_call" && JsonNode.Parse(item["arguments"]!.GetValue<string>()) is not JsonObject)
        {
            throw new ArgumentException("Response-state tool arguments must be a JSON object.");
        }
    }

    private static JsonArray ProjectTextBlocks(JsonNode? value)
    {
        if (value is not JsonArray blocks)
        {
            throw new ArgumentException("Response state content must contain ordered text blocks.");
        }

        var projected = new JsonArray();
        foreach (JsonNode? block in blocks)
        {
            string? type = block?["type"]?.GetValue<string>();
            string field = type switch
            {
                "output_text" or "summary_text" or "reasoning_text" => "text",
                "refusal" => "refusal",
                _ => throw new NotSupportedException("The adapter cannot retain this assistant content type.")
            };
            var copy = new JsonObject { ["type"] = type, [field] = block?[field]?.GetValue<string>() };
            // Annotations/log probabilities are observations, not required model context. Do not
            // retain arbitrary nested provider metadata in the replayable assistant payload.
            projected.Add(copy);
        }

        return projected;
    }
}
