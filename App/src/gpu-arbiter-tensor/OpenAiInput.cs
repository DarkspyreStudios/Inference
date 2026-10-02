using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace GpuArbiter.Tensor;

internal sealed record TextRequest(string Alias, bool Stream, bool IncludeUsage, IReadOnlyList<LlmInferenceRequest> Requests);

internal static class OpenAiInput
{
    internal static void Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ApiException(400, "Expected a JSON object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new ApiException(400, "Duplicate field", property.Name);
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new ApiException(400, "This field is not supported by the Tensor interface", property.Name);
        }
    }

    internal static string String(JsonElement value, string field, bool required = true)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ApiException(400, "Expected a JSON object");
        if (!value.TryGetProperty(field, out var item) || item.ValueKind == JsonValueKind.Null)
        {
            if (!required) return string.Empty;
            throw new ApiException(400, "Required string is missing", field);
        }
        if (item.ValueKind != JsonValueKind.String) throw new ApiException(400, "Expected a string", field);
        var result = item.GetString()!;
        if (required && string.IsNullOrWhiteSpace(result)) throw new ApiException(400, "String must not be empty", field);
        return result;
    }

    internal static bool Boolean(JsonElement value, string field, bool fallback = false) =>
        !value.TryGetProperty(field, out var item) ? fallback : item.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ApiException(400, "Expected a boolean", field)
        };

    internal static int? Integer(JsonElement value, string field)
    {
        if (!value.TryGetProperty(field, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var number) || number <= 0) throw new ApiException(400, "Expected a positive integer", field);
        return number;
    }

    private static double? Temperature(JsonElement value)
    {
        if (!value.TryGetProperty("temperature", out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0 || number > 2)
            throw new ApiException(400, "Temperature must be between 0 and 2", "temperature");
        return number;
    }

    internal static TextRequest Parse(JsonElement root, string route, HostedModel model, int maximumChoices)
    {
        bool responses = route == "/v1/responses";
        bool completions = route == "/v1/completions";
        var fields = new List<string> { "model", "stream", "temperature" };
        fields.AddRange(responses
            ? ["input", "instructions", "max_output_tokens", "reasoning", "text", "tools", "tool_choice", "parallel_tool_calls", "store"]
            : completions ? ["prompt", "max_tokens", "n", "stream_options"]
            : ["messages", "max_tokens", "max_completion_tokens", "n", "stream_options", "reasoning_effort", "response_format", "tools", "tool_choice", "parallel_tool_calls", "store"]);
        Fields(root, fields.ToArray());
        if (Boolean(root, "store")) throw new ApiException(400, "Response storage is not supported", "store");
        var count = Integer(root, "n") ?? 1;
        if (count > maximumChoices) throw new ApiException(400, "Too many choices", "n");
        bool includeUsage = false;
        if (root.TryGetProperty("stream_options", out var streamOptions))
        {
            Fields(streamOptions, "include_usage");
            includeUsage = Boolean(streamOptions, "include_usage");
        }
        var messageSets = new List<IReadOnlyList<LlmMessage>>();
        if (completions)
        {
            if (!root.TryGetProperty("prompt", out var prompts)) throw new ApiException(400, "Prompt is required", "prompt");
            var texts = prompts.ValueKind == JsonValueKind.String ? new[] { prompts.GetString()! }
                : prompts.ValueKind == JsonValueKind.Array ? prompts.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString()! : throw new ApiException(400, "Only text prompts are supported", "prompt")).ToArray()
                : throw new ApiException(400, "Only text prompts are supported", "prompt");
            foreach (var text in texts) messageSets.Add([new("user", text)]);
        }
        else if (responses)
        {
            if (!root.TryGetProperty("input", out var input)) throw new ApiException(400, "Input is required", "input");
            messageSets.Add(input.ValueKind == JsonValueKind.String ? [new LlmMessage("user", input.GetString()!)] : Messages(input, true));
        }
        else
        {
            if (!root.TryGetProperty("messages", out var messages)) throw new ApiException(400, "Messages are required", "messages");
            messageSets.Add(Messages(messages, false));
        }
        if (messageSets.Count == 0 || messageSets.Any(messages => messages.Count == 0) || (long)messageSets.Count * count > maximumChoices)
            throw new ApiException(400, "Empty input or too many prompt/choice combinations");
        if (root.TryGetProperty("max_tokens", out _) && root.TryGetProperty("max_completion_tokens", out _))
            throw new ApiException(400, "Use one output-token limit");
        var template = new LlmInferenceRequest
        {
            ModelId = model.Pack.Key.ModelId,
            Instructions = responses ? NullString(root, "instructions") : null,
            Temperature = Temperature(root),
            MaxOutputTokens = Integer(root, responses ? "max_output_tokens" : root.TryGetProperty("max_completion_tokens", out _) ? "max_completion_tokens" : "max_tokens"),
            ReasoningLevel = Reasoning(root, responses),
            StructuredOutput = Format(root, responses),
            Tools = Tools(root, responses),
            ToolChoice = Choice(root, responses),
            AllowParallelToolCalls = root.TryGetProperty("parallel_tool_calls", out _) ? Boolean(root, "parallel_tool_calls") : null
        };
        var requests = messageSets.SelectMany(messages => Enumerable.Range(0, count).Select(_ => template with { Messages = messages })).ToArray();
        return new(model.Alias, Boolean(root, "stream"), includeUsage, requests);
    }

    private static string? NullString(JsonElement value, string field) => value.TryGetProperty(field, out var item) && item.ValueKind != JsonValueKind.Null
        ? String(value, field, false) : null;

    private static IReadOnlyList<LlmMessage> Messages(JsonElement values, bool responses)
    {
        if (values.ValueKind != JsonValueKind.Array) throw new ApiException(400, "Expected a message array");
        var messages = new List<LlmMessage>();
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new ApiException(400, "Expected a message object");
            var type = NullString(item, "type");
            if (responses && type == "function_call")
            {
                Fields(item, "type", "id", "call_id", "name", "arguments", "status");
                var call = new LlmToolCall(String(item, "call_id"), String(item, "name"), String(item, "arguments", false));
                messages.Add(new("assistant", string.Empty) { ToolCalls = [call] });
                continue;
            }
            if (responses && type == "function_call_output")
            {
                Fields(item, "type", "id", "call_id", "output", "status");
                messages.Add(new("tool", string.Empty) { ToolResults = [new(String(item, "call_id"), LlmToolOutput.FromText(Content(item.GetProperty("output"))))] });
                continue;
            }
            Fields(item, "role", "content", "tool_calls", "tool_call_id", "type", "id", "status");
            if (type is not null && type != "message") throw new ApiException(400, "Unsupported input item", "type");
            var role = String(item, "role");
            role = role == "developer" ? "system" : role;
            if (role is not ("system" or "user" or "assistant" or "tool")) throw new ApiException(400, "Unsupported message role", "role");
            var content = item.TryGetProperty("content", out var body) ? Content(body) : string.Empty;
            var calls = new List<LlmToolCall>();
            if (item.TryGetProperty("tool_calls", out var toolCalls))
            {
                if (role != "assistant" || toolCalls.ValueKind != JsonValueKind.Array) throw new ApiException(400, "Invalid tool calls", "tool_calls");
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    Fields(toolCall, "id", "type", "function");
                    if (String(toolCall, "type") != "function") throw new ApiException(400, "Only function calls are supported");
                    var function = toolCall.GetProperty("function");
                    Fields(function, "name", "arguments");
                    calls.Add(new(String(toolCall, "id"), String(function, "name"), String(function, "arguments", false)));
                }
            }
            messages.Add(new(role, content)
            {
                ToolCalls = calls,
                ToolResults = role == "tool" ? [new(String(item, "tool_call_id"), LlmToolOutput.FromText(content))] : []
            });
        }
        return messages;
    }

    private static string Content(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return string.Empty;
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        if (value.ValueKind != JsonValueKind.Array) throw new ApiException(400, "Expected text content");
        var parts = new List<string>();
        foreach (var part in value.EnumerateArray())
        {
            Fields(part, "type", "text", "annotations");
            if (String(part, "type") is not ("text" or "input_text" or "output_text"))
                throw new ApiException(400, "This engine does not support this content type", "content");
            if (part.TryGetProperty("annotations", out var annotations) && (annotations.ValueKind != JsonValueKind.Array || annotations.GetArrayLength() != 0))
                throw new ApiException(400, "Annotations are not supported", "annotations");
            parts.Add(String(part, "text", false));
        }
        // Narrative content remains opaque text and is never used as markup, paths, commands or logs.
        return string.Concat(parts);
    }

    private static LlmReasoningLevel? Reasoning(JsonElement root, bool responses)
    {
        string? effort;
        if (responses)
        {
            if (!root.TryGetProperty("reasoning", out var reasoning)) return null;
            Fields(reasoning, "effort");
            effort = NullString(reasoning, "effort");
        }
        else effort = NullString(root, "reasoning_effort");
        return effort switch
        {
            null => null, "none" => LlmReasoningLevel.None, "minimal" => LlmReasoningLevel.Minimal,
            "low" => LlmReasoningLevel.Low, "medium" => LlmReasoningLevel.Medium, "high" => LlmReasoningLevel.High,
            "xhigh" => LlmReasoningLevel.ExtraHigh, _ => throw new ApiException(400, "Unsupported reasoning effort", "reasoning_effort")
        };
    }

    private static IReadOnlyList<LlmToolDefinition> Tools(JsonElement root, bool responses)
    {
        if (!root.TryGetProperty("tools", out var tools)) return [];
        if (tools.ValueKind != JsonValueKind.Array) throw new ApiException(400, "Expected a tool array", "tools");
        var result = new List<LlmToolDefinition>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (String(tool, "type") != "function") throw new ApiException(400, "Only client function tools are supported", "tools");
            Fields(tool, responses ? ["type", "name", "description", "parameters", "strict"] : ["type", "function"]);
            var function = responses ? tool : tool.GetProperty("function");
            if (!responses) Fields(function, "name", "description", "parameters", "strict");
            var schema = function.TryGetProperty("parameters", out var parameters) ? parameters.Clone()
                : JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
            if (schema.ValueKind != JsonValueKind.Object) throw new ApiException(400, "Tool schema must be an object", "parameters");
            result.Add(new() { Name = String(function, "name"), Description = NullString(function, "description"), InputSchema = schema, Strict = Boolean(function, "strict") });
        }
        return result;
    }

    private static LlmToolChoice? Choice(JsonElement root, bool responses)
    {
        if (!root.TryGetProperty("tool_choice", out var choice)) return null;
        if (choice.ValueKind == JsonValueKind.String)
            return new() { Mode = choice.GetString() switch
            { "auto" => LlmToolChoiceMode.Auto, "none" => LlmToolChoiceMode.None, "required" => LlmToolChoiceMode.Required, _ => throw new ApiException(400, "Invalid tool choice") } };
        Fields(choice, responses ? ["type", "name"] : ["type", "function"]);
        if (String(choice, "type") != "function") throw new ApiException(400, "Only function tool choice is supported");
        var function = responses ? choice : choice.GetProperty("function");
        if (!responses) Fields(function, "name");
        return new() { Mode = LlmToolChoiceMode.Specific, Name = String(function, "name") };
    }

    private static LlmStructuredOutputRequest? Format(JsonElement root, bool responses)
    {
        JsonElement format;
        if (responses)
        {
            if (!root.TryGetProperty("text", out var text)) return null;
            Fields(text, "format");
            if (!text.TryGetProperty("format", out format)) return null;
        }
        else if (!root.TryGetProperty("response_format", out format)) return null;
        var type = String(format, "type");
        if (type == "text") { Fields(format, "type"); return null; }
        if (type == "json_object")
        {
            Fields(format, "type");
            return new() { Name = "json_object", Schema = JsonSerializer.SerializeToElement(new { type = "object" }), Strict = false, Fallback = LlmStructuredOutputFallback.JsonObject };
        }
        if (type != "json_schema") throw new ApiException(400, "Unsupported output format", "response_format");
        Fields(format, responses ? ["type", "name", "description", "schema", "strict"] : ["type", "json_schema"]);
        var schema = responses ? format : format.GetProperty("json_schema");
        if (!responses) Fields(schema, "name", "description", "schema", "strict");
        if (!schema.TryGetProperty("schema", out var definition) || definition.ValueKind != JsonValueKind.Object)
            throw new ApiException(400, "JSON schema is required", "schema");
        return new() { Name = String(schema, "name"), Description = NullString(schema, "description"), Schema = definition.Clone(), Strict = Boolean(schema, "strict", true) };
    }
}
