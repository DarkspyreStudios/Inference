using Darkspyre.Inference.Common.LLM;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Versioned, account-bound output only. Never stores an unfiltered provider response.</summary>
internal static class ProviderContinuationStateCodec
{
    internal static string ImplementationId(LlmTransport transport) => $"darkspyre.connect.llm.{transport}";

    public static LlmContinuationState Encode(
        LlmConnection connection,
        string modelId,
        JsonArray output) => connection.ContinuationCodec is { } codec ? codec.Encode(connection, modelId, output) : new()
        {
            ImplementationId = ImplementationId(connection.Transport),
            ModelId = modelId,
            ScopeId = Scope(connection),
            Payload = connection.Transport == LlmTransport.AnthropicMessages
                ? AnthropicContinuation.EncodePayload(Project(output, connection.Transport))
                : Project(output, connection.Transport).ToJsonString()
        };

    /// <summary>
    /// Transports whose adapters keep continuation state. Bedrock Converse state carries the token counts the provider
    /// reported, and the ordered reply when the reply contains reasoning.
    /// </summary>
    public static bool Retains(LlmTransport transport) =>
        transport is LlmTransport.OpenAiResponses or LlmTransport.OpenAiCodex or LlmTransport.AnthropicMessages
            or LlmTransport.AwsBedrockConverse;

    private const string NoOutput = "[]";

    /// <summary>
    /// State with no replayable output. It carries the token counts the provider reported, so accounting can count
    /// the history it closes. Bedrock Converse writes it for a reply without reasoning.
    /// </summary>
    public static LlmContinuationState EncodeCounts(
        LlmConnection connection,
        string modelId,
        long inputTokens,
        long outputTokens) => new()
        {
            ImplementationId = ImplementationId(connection.Transport),
            ModelId = modelId,
            ScopeId = Scope(connection),
            Payload = NoOutput,
            InputTokens = inputTokens,
            OutputTokens = outputTokens
        };

    /// <summary>Whether this state can be replayed on the connection and model.</summary>
    public static bool IsBound(LlmContinuationState state, LlmConnection connection, string modelId) =>
        connection.ContinuationCodec is { } codec ? codec.IsBound(state, connection, modelId)
        : Retains(connection.Transport) && state.FormatVersion == 2 && state.ImplementationId == ImplementationId(connection.Transport)
        && state.ModelId == modelId && state.ScopeId == Scope(connection);

    public static JsonArray Decode(
        LlmContinuationState state,
        LlmConnection connection,
        string modelId)
    {
        if (state.Payload is null || state.Payload.Length > 16 * 1024 * 1024 || !IsBound(state, connection, modelId))
        {
            throw new ArgumentException("Response state does not match this version, transport, model, or account scope.");
        }
        if (connection.Transport == LlmTransport.AwsBedrockConverse && state.Payload == NoOutput)
        {
            return [];
        }

        try
        {
            JsonArray output = connection.Transport == LlmTransport.AnthropicMessages
                ? AnthropicContinuation.ReadPayload(state.Payload).Content
                : JsonNode.Parse(state.Payload) as JsonArray
                    ?? throw new ArgumentException("Response state must contain ordered assistant output.");

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
        if (connection.ContinuationCodec is { } codec)
        {
            return codec.DecodeMessage(message, connection, modelId);
        }
        JsonArray output = Decode(message.ContinuationState!, connection, modelId);
        if (connection.Transport == LlmTransport.AwsBedrockConverse && output.Count == 0)
        {
            return output;
        }
        string text = connection.Transport is LlmTransport.AnthropicMessages or LlmTransport.AwsBedrockConverse
            ? string.Concat(output.OfType<JsonObject>()
                .Where(item => item["type"]?.GetValue<string>() == "text")
                .Select(item => item["text"]?.GetValue<string>()))
            : string.Concat(output.OfType<JsonObject>()
                .Where(item => item["type"]?.GetValue<string>() == "message")
                .SelectMany(item => (item["content"] as JsonArray ?? []).OfType<JsonObject>())
                .Where(part => part["type"]?.GetValue<string>() == "output_text")
                .Select(part => part["text"]?.GetValue<string>()));
        if (!string.Equals(text, message.Content, StringComparison.Ordinal))
        {
            throw new ArgumentException("Assistant text must match its opaque continuation state.");
        }
        var calls = output.OfType<JsonObject>()
            .Where(item => item["type"]?.GetValue<string>() is "function_call" or "tool_use").ToArray();
        if (calls.Length != message.ToolCalls.Count)
        {
            throw new ArgumentException("Assistant tool calls must match its opaque continuation state.");
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
                    throw new ArgumentException("Assistant tool calls must match its opaque continuation state.");
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
        if (connection.ResolvedContinuationScope is { } scope) { return scope; }
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        // The concurrency Version and rotating OAuth tokens are not account identities. Missing an
        // OAuth account ID binds conservatively to its current token; never guess account equivalence.
        var identityValue = new ConnectContinuationScopeIdentity(connection.Transport, connection.Endpoint,
            credential?.ApiKey, credential?.OAuth?.AccountId ?? credential?.OAuth?.AccessToken,
            connection.AdditionalHeaders.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new ConnectContinuationScopeHeader(pair.Key.ToLowerInvariant(), pair.Value)).ToArray(),
            credential?.SecretHeaders.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new ConnectContinuationScopeHeader(pair.Key.ToLowerInvariant(), pair.Value)).ToArray());
        string identity = JsonSerializer.Serialize(identityValue, ConnectLlmJsonContext.Default.ConnectContinuationScopeIdentity);
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
                (LlmTransport.AnthropicMessages, "text") => ["type", "text", "citations"],
                (LlmTransport.AnthropicMessages, "thinking") => ["type", "thinking", "signature"],
                (LlmTransport.AnthropicMessages, "redacted_thinking") => ["type", "data"],
                (LlmTransport.AnthropicMessages, "tool_use") => ["type", "id", "name", "input", "caller"],
                (LlmTransport.AnthropicMessages, "server_tool_use") => ["type", "id", "name", "input", "caller"],
                (LlmTransport.AnthropicMessages, "web_search_tool_result" or "web_fetch_tool_result"
                    or "code_execution_tool_result" or "bash_code_execution_tool_result"
                    or "text_editor_code_execution_tool_result" or "tool_search_tool_result" or "advisor_tool_result") =>
                    ["type", "tool_use_id", "content", "caller"],
                (LlmTransport.AwsBedrockConverse, "text") => ["type", "text"],
                (LlmTransport.AwsBedrockConverse, "reasoning") => ["type", "text", "signature"],
                (LlmTransport.AwsBedrockConverse, "redacted_reasoning") => ["type", "data"],
                (LlmTransport.AwsBedrockConverse, "tool_use") => ["type", "id", "name", "input"],
                _ => throw new NotSupportedException("The adapter cannot retain this assistant output type.")
            };
            var copy = new JsonObject();
            foreach (string field in fields)
            {
                if (item.TryGetPropertyValue(field, out JsonNode? value))
                {
                    if (transport == LlmTransport.AnthropicMessages && field == "citations")
                    {
                        if (value is not JsonArray citations || citations.Any(citation => citation is not JsonObject))
                        { throw new ArgumentException("Assistant citations in continuation state must be ordered JSON objects."); }
                        copy[field] = citations.DeepClone();
                    }
                    else if (transport == LlmTransport.AnthropicMessages && field is "content" or "caller")
                    {
                        // Provider-owned results/caller metadata are opaque replay data. Their strings,
                        // URLs and code are never interpreted, executed or logged by the library.
                        if (value is not JsonObject && (field != "content" || value is not JsonArray))
                        { throw new ArgumentException("Server-tool continuation content must be a JSON object or ordered block array."); }
                        copy[field] = value.DeepClone();
                    }
                    else if (field is "content" or "summary")
                    {
                        copy[field] = ProjectTextBlocks(value);
                    }
                    else if (field == "input")
                    {
                        if (value is not JsonObject)
                        {
                            throw new ArgumentException("Tool input in continuation state must be a JSON object.");
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
            ValidateItem(copy, transport, type);
            projected.Add(copy);
        }

        return projected;
    }

    private static void ValidateItem(JsonObject item, LlmTransport transport, string type)
    {
        string[] required = (transport, type) switch
        {
            (_, "message") => ["content"],
            (_, "function_call") => ["call_id", "name", "arguments"],
            (_, "tool_use" or "server_tool_use") => ["id", "name", "input"],
            (LlmTransport.AnthropicMessages, "web_search_tool_result" or "web_fetch_tool_result"
                or "code_execution_tool_result" or "bash_code_execution_tool_result"
                or "text_editor_code_execution_tool_result" or "tool_search_tool_result" or "advisor_tool_result") => ["tool_use_id", "content"],
            (_, "thinking") => ["thinking", "signature"],
            (_, "redacted_thinking") => ["data"],
            (_, "text") => ["text"],
            // Bedrock reasoning text may be empty: a signed block under an omitted display carries none.
            (LlmTransport.AwsBedrockConverse, "reasoning") => ["text"],
            (LlmTransport.AwsBedrockConverse, "redacted_reasoning") => ["data"],
            _ => []
        };
        if (required.Any(field => item[field] is null)
            || (type is "function_call" or "tool_use" or "server_tool_use"
                && (string.IsNullOrWhiteSpace(item[type == "function_call" ? "call_id" : "id"]!.GetValue<string>())
                    || string.IsNullOrWhiteSpace(item["name"]!.GetValue<string>()))))
        {
            throw new ArgumentException("Response state is missing required assistant output fields.");
        }
        if (item["tool_use_id"] is { } toolUseId && string.IsNullOrWhiteSpace(toolUseId.GetValue<string>()))
        { throw new ArgumentException("Server-tool continuation state requires a tool-use identity."); }
        if (type == "function_call" && JsonNode.Parse(item["arguments"]!.GetValue<string>()) is not JsonObject)
        {
            throw new ArgumentException("Response-state tool arguments must be a JSON object.");
        }
        if (transport == LlmTransport.AwsBedrockConverse && type == "redacted_reasoning")
        {
            try
            {
                _ = Convert.FromBase64String(item["data"]!.GetValue<string>());
            }
            catch (FormatException)
            {
                throw new ArgumentException("Response-state redacted reasoning must be base64.");
            }
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
