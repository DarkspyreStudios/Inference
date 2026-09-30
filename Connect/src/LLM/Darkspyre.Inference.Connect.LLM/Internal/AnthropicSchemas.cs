using System.Text.Json;
using System.Text.RegularExpressions;
using Darkspyre.Inference.Common.LLM;
using Json.Pointer;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class AnthropicSchemas
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "type", "title", "description", "default", "properties", "additionalProperties", "required",
        "items", "minItems", "enum", "const", "anyOf", "allOf", "$ref", "$defs", "definitions", "$schema",
        "format", "pattern"
    };
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    { "object", "array", "string", "integer", "number", "boolean", "null" };
    private static readonly HashSet<string> Formats = new(StringComparer.Ordinal)
    { "date-time", "time", "date", "duration", "email", "hostname", "uri", "ipv4", "ipv6", "uuid" };

    internal static LlmStructuredOutputEnforcement? Enforcement(LlmStructuredOutputRequest? request,
        LlmProviderCapabilities capabilities)
    {
        if (request is null) return null;
        return StructuredOutput.ResolveEnforcement(request, capabilities with
        {
            SupportsJsonSchema = capabilities.SupportsJsonSchema && Supports(request.Schema),
            SupportsJsonObject = false
        });
    }

    internal static void ValidateTools(LlmChatRequest request)
    {
        if (request.Tools.Count(tool => tool.Strict) > 20)
        { throw new NotSupportedException("Claude accepts at most 20 strict tools in one request."); }
        if (request.Tools.Any(tool => tool.Strict && !Supports(tool.InputSchema)))
        { throw new NotSupportedException("A strict tool schema contains constraints outside Claude's native subset. The tool schema is not weakened or sent as non-strict."); }
    }

    internal static bool Supports(JsonElement schema)
    {
        var active = new HashSet<SchemaNode>();
        var visited = new HashSet<SchemaNode>();
        var pending = new Stack<(SchemaNode Node, bool Leaving)>();
        pending.Push((new(schema, false), false));
        while (pending.TryPop(out var next))
        {
            if (next.Leaving)
            {
                active.Remove(next.Node);
                visited.Add(next.Node);
                continue;
            }
            if (visited.Contains(next.Node)) continue;
            if (!active.Add(next.Node)) return false;
            var children = new List<SchemaNode>();
            if (!Check(next.Node.Schema, schema, next.Node.InsideAllOf, children)) return false;
            pending.Push((next.Node, true));
            for (int index = children.Count - 1; index >= 0; index--) pending.Push((children[index], false));
        }
        return true;
    }

    private readonly record struct SchemaNode(JsonElement Schema, bool InsideAllOf);

    private static bool Check(JsonElement schema, JsonElement root, bool insideAllOf, List<SchemaNode> children)
    {
        if (schema.ValueKind != JsonValueKind.Object || insideAllOf && schema.TryGetProperty("$ref", out _)) return false;
        bool isObject = schema.TryGetProperty("properties", out _);
        foreach (JsonProperty property in schema.EnumerateObject())
        {
            if (!Keywords.Contains(property.Name)) return false;
            JsonElement value = property.Value;
            switch (property.Name)
            {
                case "type":
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        if (!Types.Contains(value.GetString()!)) return false;
                        isObject |= value.GetString() == "object";
                    }
                    else if (value.ValueKind == JsonValueKind.Array)
                    {
                        if (value.GetArrayLength() == 0 || value.EnumerateArray()
                            .Any(type => type.ValueKind != JsonValueKind.String || !Types.Contains(type.GetString()!))) return false;
                        isObject |= value.EnumerateArray().Any(type => type.GetString() == "object");
                    }
                    else return false;
                    break;
                case "properties":
                case "$defs":
                case "definitions":
                    if (value.ValueKind != JsonValueKind.Object) return false;
                    foreach (JsonProperty child in value.EnumerateObject()) children.Add(new(child.Value, insideAllOf));
                    break;
                case "items":
                    children.Add(new(value, insideAllOf));
                    break;
                case "anyOf":
                case "allOf":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) return false;
                    foreach (JsonElement child in value.EnumerateArray()) children.Add(new(child, insideAllOf || property.Name == "allOf"));
                    break;
                case "$ref":
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } reference
                        || !reference.StartsWith('#') || !JsonPointer.TryParse(reference, out JsonPointer pointer)
                        || pointer.Evaluate(root) is not { } target) return false;
                    children.Add(new(target, insideAllOf));
                    break;
                case "additionalProperties":
                    if (value.ValueKind != JsonValueKind.False) return false;
                    break;
                case "required":
                    if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)) return false;
                    break;
                case "minItems":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int minimum) || minimum is < 0 or > 1) return false;
                    break;
                case "enum":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0 || value.EnumerateArray()
                        .Any(item => item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)) return false;
                    break;
                case "format":
                    if (value.ValueKind != JsonValueKind.String || !Formats.Contains(value.GetString()!)) return false;
                    break;
                case "pattern":
                    if (value.ValueKind != JsonValueKind.String || !SupportsPattern(value.GetString()!)) return false;
                    break;
                case "title":
                case "description":
                case "$schema":
                    if (value.ValueKind != JsonValueKind.String) return false;
                    break;
            }
        }
        return !isObject || schema.TryGetProperty("additionalProperties", out JsonElement additional)
            && additional.ValueKind == JsonValueKind.False;
    }

    private static bool SupportsPattern(string pattern)
    {
        try { _ = new Regex(pattern, RegexOptions.ECMAScript, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException) { return false; }
        bool characterClass = false;
        // The runtime parses regex syntax; this pass classifies provider-unsupported constructs.
        for (int index = 0; index < pattern.Length; index++)
        {
            char value = pattern[index];
            if (value == '\\' && ++index < pattern.Length)
            {
                char escaped = pattern[index];
                if (escaped is >= '1' and <= '9' or 'k' || !characterClass && escaped is 'b' or 'B') return false;
            }
            else if (value == '[') characterClass = true;
            else if (value == ']') characterClass = false;
            else if (!characterClass && value == '(' && index + 1 < pattern.Length && pattern[index + 1] == '?'
                && (index + 2 >= pattern.Length || pattern[index + 2] != ':')) return false;
        }
        return true;
    }
}
