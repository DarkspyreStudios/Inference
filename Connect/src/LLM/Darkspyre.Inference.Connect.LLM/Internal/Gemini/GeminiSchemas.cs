using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal enum GeminiSchemaDialect { GeminiApi, Vertex }

internal static class GeminiSchemas
{
    // Google silently ignores unsupported output keywords. Validate without rewriting the host schema.
    private static readonly HashSet<string> VertexKeywords = new(StringComparer.Ordinal)
    { "type", "format", "title", "description", "enum", "items", "minItems", "maxItems", "minimum", "maximum", "anyOf", "properties", "additionalProperties", "required", "propertyOrdering" };
    private static readonly HashSet<string> ApiKeywords = new(VertexKeywords, StringComparer.Ordinal)
    { "$id", "$defs", "$ref", "$anchor", "prefixItems", "oneOf" };

    public static LlmStructuredOutputEnforcement? Enforcement(LlmStructuredOutputRequest? request, string routeName, GeminiSchemaDialect dialect)
    {
        if (request is null) { return null; }
        if (Supports(request.Schema, dialect)) { return LlmStructuredOutputEnforcement.ProviderJsonSchema; }
        if (request.Fallback == LlmStructuredOutputFallback.LocalValidation)
        { return LlmStructuredOutputEnforcement.LocalValidationOnly; }
        throw new NotSupportedException($"The output schema contains a keyword not enforced by this {routeName} route. Explicit local validation fallback is required.");
    }

    public static bool Supports(JsonElement schema, GeminiSchemaDialect dialect)
    {
        if (schema.ValueKind == JsonValueKind.False || schema.ValueKind == JsonValueKind.True) { return true; }
        if (schema.ValueKind != JsonValueKind.Object) { return false; }
        if (dialect == GeminiSchemaDialect.GeminiApi && schema.TryGetProperty("$ref", out JsonElement reference)
            && (reference.ValueKind != JsonValueKind.String || !reference.GetString()!.StartsWith('#')
                || schema.EnumerateObject().Any(property => !property.Name.StartsWith('$')))) { return false; }
        foreach (JsonProperty property in schema.EnumerateObject())
        {
            if (!(dialect == GeminiSchemaDialect.GeminiApi ? ApiKeywords : VertexKeywords).Contains(property.Name)) { return false; }
            if (property.Name is "properties" or "$defs" && (property.Value.ValueKind != JsonValueKind.Object
                || property.Value.EnumerateObject().Any(child => !Supports(child.Value, dialect)))) { return false; }
            if (property.Name is "items" or "additionalProperties" && !Supports(property.Value, dialect)) { return false; }
            if (property.Name is "anyOf" or "oneOf" or "prefixItems" && (property.Value.ValueKind != JsonValueKind.Array
                || property.Value.EnumerateArray().Any(child => !Supports(child, dialect)))) { return false; }
            if (property.Name is "$id" or "$anchor" && property.Value.ValueKind != JsonValueKind.String) { return false; }
            if (property.Name == "type" && property.Value.ValueKind != JsonValueKind.String
                && (dialect != GeminiSchemaDialect.GeminiApi || property.Value.ValueKind != JsonValueKind.Array
                    || property.Value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))) { return false; }
            if (property.Name == "enum" && (property.Value.ValueKind != JsonValueKind.Array
                || property.Value.EnumerateArray().Any(item => item.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)))) { return false; }
        }
        return true;
    }
}
