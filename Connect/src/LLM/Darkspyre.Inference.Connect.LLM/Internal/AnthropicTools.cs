using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Claude-owned server definitions and metadata augment typed client tools, never replace them.</summary>
internal sealed class AnthropicTools
{
    internal const string OptionName = "anthropic_tools";
    private readonly JsonArray serverTools = [];
    private readonly Dictionary<string, JsonElement> clientOptions = new(StringComparer.Ordinal);
    internal HashSet<string> NativeNames { get; } = new(StringComparer.Ordinal);

    internal static AnthropicTools Resolve(LlmConnection connection, LlmChatRequest request)
    {
        if (connection.ProviderOptions.Keys.Concat(request.ProviderOptions.Keys).Any(name => name is "tools" or "tool_choice"))
            throw new NotSupportedException("Claude tools use typed Tools and anthropic_tools; tool choice uses typed controls.");
        var result = new AnthropicTools();
        if (!request.ProviderOptions.TryGetValue(OptionName, out JsonElement options)
            && !connection.ProviderOptions.TryGetValue(OptionName, out options)) return result;
        RequireObject(options);
        if (options.EnumerateObject().Any(field => field.Name is not ("server_tools" or "client_tools")))
            throw Invalid();

        var allNames = new HashSet<string>(request.Tools.Select(tool => tool.Name), StringComparer.Ordinal);
        if (options.TryGetProperty("server_tools", out JsonElement native))
        {
            if (native.ValueKind != JsonValueKind.Array) throw Invalid();
            foreach (JsonElement tool in native.EnumerateArray())
            {
                RequireObject(tool);
                string? type = String(tool, "type"), name = String(tool, "name");
                if (type is null || name != ServerName(type)) throw Invalid();
                if (tool.TryGetProperty("input_schema", out _) || tool.TryGetProperty("strict", out _))
                    throw new NotSupportedException("Claude client schemas and strictness belong to typed Tools.");
                if (!allNames.Add(name)) throw new ArgumentException("Claude tool names must be unique.");
                if (tool.TryGetProperty("allowed_callers", out JsonElement callers)) ValidateCallers(callers);
                result.NativeNames.Add(name);
                // Provider configuration stays opaque JSON, never executable host commands or diagnostics.
                result.serverTools.Add(JsonNode.Parse(tool.GetRawText()));
            }
        }

        bool hasProgrammatic = false;
        if (options.TryGetProperty("client_tools", out JsonElement clients))
        {
            RequireObject(clients);
            foreach (JsonProperty field in clients.EnumerateObject())
            {
                LlmToolDefinition? definition = request.Tools.FirstOrDefault(tool => tool.Name == field.Name);
                if (definition is null) throw new ArgumentException("Claude client metadata must name a typed request tool.");
                RequireObject(field.Value);
                if (field.Value.EnumerateObject().Any(property => property.Name is not ("allowed_callers" or "defer_loading" or "input_examples")))
                    throw Invalid();
                if (field.Value.TryGetProperty("allowed_callers", out JsonElement callers))
                {
                    ValidateCallers(callers);
                    bool programmatic = callers.EnumerateArray().Any(caller => caller.GetString() != "direct");
                    hasProgrammatic |= programmatic;
                    if (programmatic && !result.serverTools.Any(tool => (string?)tool!["type"] is "code_execution_20260120" or "code_execution_20260521"))
                        throw new ArgumentException("Programmatic Claude clients require a compatible code-execution tool.");
                    if (programmatic && definition.Strict)
                        throw new NotSupportedException("Programmatic Claude client tools do not support strict input schemas.");
                    if (request.ToolChoice is { Mode: LlmToolChoiceMode.Specific } choice && choice.Name == field.Name
                        && !callers.EnumerateArray().Any(caller => caller.GetString() == "direct"))
                        throw new NotSupportedException("A forced Claude client tool must allow direct calling.");
                }
                if (field.Value.TryGetProperty("defer_loading", out JsonElement deferred))
                {
                    if (deferred.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
                    if (deferred.GetBoolean() && !result.serverTools.Any(tool => (string?)tool!["type"] is
                        "tool_search_tool_bm25_20251119" or "tool_search_tool_regex_20251119" or "tool_search_tool_bm25" or "tool_search_tool_regex"))
                        throw new ArgumentException("Deferred Claude clients require a tool-search tool.");
                }
                if (field.Value.TryGetProperty("input_examples", out JsonElement examples)
                    && (examples.ValueKind != JsonValueKind.Array || examples.EnumerateArray().Any(example => example.ValueKind != JsonValueKind.Object)))
                    throw Invalid();
                result.clientOptions.Add(field.Name, field.Value);
            }
        }
        if (hasProgrammatic && request.AllowParallelToolCalls == false)
            throw new NotSupportedException("Programmatic Claude client tools cannot disable parallel tool use.");
        return result;
    }

    internal JsonArray Build(IReadOnlyList<LlmToolDefinition> definitions)
    {
        var tools = new JsonArray();
        foreach (LlmToolDefinition definition in definitions)
        {
            var tool = new JsonObject
            {
                ["name"] = definition.Name,
                ["input_schema"] = JsonNode.Parse(definition.InputSchema.GetRawText())
            };
            // Non-strict models can reject the field even when it is false.
            if (definition.Strict) tool["strict"] = true;
            if (!string.IsNullOrWhiteSpace(definition.Description)) tool["description"] = definition.Description;
            if (clientOptions.TryGetValue(definition.Name, out JsonElement options))
                foreach (JsonProperty field in options.EnumerateObject()) tool[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            tools.Add(tool);
        }
        foreach (JsonNode? tool in serverTools) tools.Add(tool!.DeepClone());
        return tools;
    }

    private static string ServerName(string type) => type switch
    {
        "web_search_20250305" or "web_search_20260209" or "web_search_20260318" => "web_search",
        "web_fetch_20250910" or "web_fetch_20260209" or "web_fetch_20260309" or "web_fetch_20260318" => "web_fetch",
        "code_execution_20250522" or "code_execution_20250825" or "code_execution_20260120" or "code_execution_20260521" => "code_execution",
        "tool_search_tool_bm25_20251119" or "tool_search_tool_bm25" => "tool_search_tool_bm25",
        "tool_search_tool_regex_20251119" or "tool_search_tool_regex" => "tool_search_tool_regex",
        "advisor_20260301" => "advisor",
        _ => throw new NotSupportedException("Unknown or client-executed Claude native tool type.")
    };

    private static void ValidateCallers(JsonElement callers)
    {
        if (callers.ValueKind != JsonValueKind.Array || callers.GetArrayLength() == 0) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement caller in callers.EnumerateArray())
        {
            if (caller.ValueKind != JsonValueKind.String || caller.GetString() is not ("direct" or "code_execution_20260120" or "code_execution_20260521"))
                throw Invalid();
            if (!seen.Add(caller.GetString()!)) throw Invalid();
        }
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty field in value.EnumerateObject()) if (!names.Add(field.Name)) throw Invalid();
    }

    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out JsonElement field)
        && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static ArgumentException Invalid() => new("Invalid Claude native-tool configuration.");
}
