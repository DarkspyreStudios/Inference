using Darkspyre.Inference.Common;
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

    internal static LlmStructuredOutputEnforcement? Enforcement(LlmChatRequest request,
        LlmProviderCapabilities capabilities)
    {
        SchemaCost tools = StrictToolCost(request);
        if (request.StructuredOutput is not { } output) return null;
        return StructuredOutput.ResolveEnforcement(output, capabilities with
        {
            SupportsJsonSchema = ((capabilities.SupportsJsonSchema != InferenceSupport.Unsupported) && Analyze(output.Schema, out SchemaCost cost)
                && tools.Add(cost).Fits ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsJsonObject = InferenceSupport.Unsupported
        });
    }

    private static SchemaCost StrictToolCost(LlmChatRequest request)
    {
        if (ClientTools.Functions(request.Tools).Count(tool => tool.Strict) > 20)
        { throw new NotSupportedException("Claude accepts at most 20 strict tools in one request."); }
        SchemaCost total = default;
        foreach (LlmToolDefinition tool in ClientTools.Functions(request.Tools).Where(tool => tool.Strict))
        {
            if (!Analyze(tool.InputSchema, out SchemaCost cost))
            { throw new NotSupportedException("A strict tool schema contains constraints outside Claude's native subset. The tool schema is not weakened or sent as non-strict."); }
            total = total.Add(cost);
            if (!total.Fits)
            { throw new NotSupportedException("Claude accepts at most 24 optional parameters and 16 union parameters across strict schemas. Strict tools are not weakened to fit these limits."); }
        }
        return total;
    }

    private static bool Analyze(JsonElement schema, out SchemaCost cost)
    {
        cost = default;
        var active = new HashSet<SchemaNode>();
        var analyzed = new Dictionary<SchemaNode, SchemaCost>();
        var pending = new Stack<(SchemaNode Node, List<SchemaEdge>? Children)>();
        var root = new SchemaNode(schema, false);
        pending.Push((root, null));
        while (pending.TryPop(out var next))
        {
            if (next.Children is { } children)
            {
                SchemaCost current = SchemaCost.Local(next.Node.Schema);
                foreach (SchemaEdge child in children.Where(child => child.Counts))
                    current = current.Add(analyzed[child.Node]);
                active.Remove(next.Node);
                analyzed.Add(next.Node, current);
                continue;
            }
            if (analyzed.ContainsKey(next.Node)) continue;
            if (!active.Add(next.Node)) return false;
            var edges = new List<SchemaEdge>();
            if (!Check(next.Node.Schema, schema, next.Node.InsideAllOf, edges)) return false;
            pending.Push((next.Node, edges));
            for (int index = edges.Count - 1; index >= 0; index--) pending.Push((edges[index].Node, null));
        }
        cost = analyzed[root];
        return true;
    }

    private readonly record struct SchemaNode(JsonElement Schema, bool InsideAllOf);
    private readonly record struct SchemaEdge(SchemaNode Node, bool Counts);

    private readonly record struct SchemaCost(int Optional, int Unions)
    {
        internal bool Fits => Optional <= 24 && Unions <= 16;

        // Saturation bounds work even when a shared-reference graph expands exponentially.
        internal SchemaCost Add(SchemaCost other) => new(Math.Min(25, Optional + other.Optional), Math.Min(17, Unions + other.Unions));

        internal static SchemaCost Local(JsonElement schema)
        {
            int optional = 0;
            if (schema.TryGetProperty("properties", out JsonElement properties))
            {
                var required = schema.TryGetProperty("required", out JsonElement names)
                    ? names.EnumerateArray().Select(name => name.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
                foreach (JsonProperty property in properties.EnumerateObject())
                    if (!required.Contains(property.Name) && ++optional > 24) break;
            }
            bool union = schema.TryGetProperty("anyOf", out _) || schema.TryGetProperty("type", out JsonElement type)
                && type.ValueKind == JsonValueKind.Array;
            return new(optional, union ? 1 : 0);
        }
    }

    private static bool Check(JsonElement schema, JsonElement root, bool insideAllOf, List<SchemaEdge> children)
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
                    foreach (JsonProperty child in value.EnumerateObject())
                        children.Add(new(new(child.Value, insideAllOf), property.Name == "properties"));
                    break;
                case "items":
                    children.Add(new(new(value, insideAllOf), true));
                    break;
                case "anyOf":
                case "allOf":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) return false;
                    foreach (JsonElement child in value.EnumerateArray())
                        children.Add(new(new(child, insideAllOf || property.Name == "allOf"), true));
                    break;
                case "$ref":
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } reference
                        || !reference.StartsWith('#') || !JsonPointer.TryParse(reference, out JsonPointer pointer)
                        || pointer.Evaluate(root) is not { } target) return false;
                    children.Add(new(new(target, insideAllOf), true));
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
        // Unpublished range and grammar-size limits remain provider errors; no cutoff is invented.
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
