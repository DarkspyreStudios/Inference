using System.Text.Json;
using Json.Pointer;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Checks published numeric guarantees without inspecting literal values or changing the schema.</summary>
internal static class XaiSchemaLimits
{
    internal static LlmStructuredOutputEnforcement? ResolveEnforcement(LlmStructuredOutputRequest? output,
        LlmProviderCapabilities capabilities) => output is null ? null : StructuredOutput.ResolveEnforcement(output,
        Fits(output.Schema) ? capabilities : capabilities with { SupportsJsonSchema = false, SupportsJsonObject = false });

    internal static bool Fits(JsonElement root)
    {
        var pending = new Stack<(JsonElement Schema, bool Exit)>();
        var active = new HashSet<JsonElement>();
        var done = new HashSet<JsonElement>();
        pending.Push((root, false));
        while (pending.TryPop(out var next))
        {
            JsonElement schema = next.Schema;
            if (next.Exit) { active.Remove(schema); done.Add(schema); continue; }
            if (done.Contains(schema) || schema.ValueKind is JsonValueKind.True or JsonValueKind.False) continue;
            if (!active.Add(schema)) return false;
            if (schema.ValueKind != JsonValueKind.Object) return false;
            pending.Push((schema, true));
            foreach (JsonProperty property in schema.EnumerateObject())
            {
                JsonElement value = property.Value;
                switch (property.Name)
                {
                    case "minLength":
                    case "maxLength":
                        if (!Within(value, 2048)) return false;
                        break;
                    case "minItems":
                    case "maxItems":
                        if (!Within(value, 256)) return false;
                        break;
                    case "minProperties":
                    case "maxProperties":
                        if (!Within(value, 64)) return false;
                        break;
                    case "$ref":
                        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } reference
                            || !reference.StartsWith('#') || !JsonPointer.TryParse(reference, out JsonPointer pointer)
                            || pointer.Evaluate(root) is not { } target) return false;
                        pending.Push((target, false));
                        break;
                    case "properties":
                    case "patternProperties":
                    case "dependentSchemas":
                        if (value.ValueKind != JsonValueKind.Object) return false;
                        foreach (JsonProperty child in value.EnumerateObject()) pending.Push((child.Value, false));
                        break;
                    case "allOf":
                    case "anyOf":
                    case "oneOf":
                    case "prefixItems":
                        if (value.ValueKind != JsonValueKind.Array) return false;
                        foreach (JsonElement child in value.EnumerateArray()) pending.Push((child, false));
                        break;
                    case "items":
                    case "additionalItems":
                    case "additionalProperties":
                    case "contains":
                    case "not":
                    case "if":
                    case "then":
                    case "else":
                    case "propertyNames":
                    case "unevaluatedItems":
                    case "unevaluatedProperties":
                        if (value.ValueKind == JsonValueKind.Array)
                        { foreach (JsonElement child in value.EnumerateArray()) pending.Push((child, false)); }
                        else pending.Push((value, false));
                        break;
                }
            }
        }
        return true;
    }

    private static bool Within(JsonElement value, int maximum) => value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out decimal number) && number == decimal.Truncate(number) && number >= 0 && number <= maximum;
}
