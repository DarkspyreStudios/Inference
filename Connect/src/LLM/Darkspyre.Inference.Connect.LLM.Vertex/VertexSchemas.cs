using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal static class VertexSchemas
{
    // Google silently ignores unsupported output keywords. Validate without rewriting the host schema.
    private static readonly HashSet<string> GeminiKeywords = new(StringComparer.Ordinal)
    { "type", "format", "title", "description", "enum", "items", "minItems", "maxItems", "minimum", "maximum", "anyOf", "properties", "additionalProperties", "required", "propertyOrdering" };

    public static LlmStructuredOutputEnforcement? Enforcement(LlmStructuredOutputRequest? request, bool claude)
    {
        if (request is null) { return null; }
        bool supported = claude || GeminiSchema(request.Schema);
        if (supported) { return LlmStructuredOutputEnforcement.ProviderJsonSchema; }
        if (request.Fallback == LlmStructuredOutputFallback.LocalValidation)
        { return LlmStructuredOutputEnforcement.LocalValidationOnly; }
        throw new NotSupportedException("The output schema contains a keyword not enforced by this Vertex route. Explicit local validation fallback is required.");
    }

    private static bool GeminiSchema(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.False || schema.ValueKind == JsonValueKind.True) { return true; }
        if (schema.ValueKind != JsonValueKind.Object) { return false; }
        foreach (JsonProperty property in schema.EnumerateObject())
        {
            if (!GeminiKeywords.Contains(property.Name)) { return false; }
            if (property.Name == "properties" && (property.Value.ValueKind != JsonValueKind.Object
                || property.Value.EnumerateObject().Any(child => !GeminiSchema(child.Value)))) { return false; }
            if (property.Name is "items" or "additionalProperties" && !GeminiSchema(property.Value)) { return false; }
            if (property.Name == "anyOf" && (property.Value.ValueKind != JsonValueKind.Array
                || property.Value.EnumerateArray().Any(child => !GeminiSchema(child)))) { return false; }
            if (property.Name == "enum" && (property.Value.ValueKind != JsonValueKind.Array
                || property.Value.EnumerateArray().Any(item => item.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)))) { return false; }
        }
        return true;
    }
}
