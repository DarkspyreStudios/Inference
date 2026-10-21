using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// The tool schema a provider receives and the arguments the evaluator and handler see. Provider tool arguments are an
/// object, so a schema whose root is not an object is sent wrapped in an object with one required member, <c>value</c>,
/// and that member is unwrapped from the model's arguments. A host tool's schema is sent without
/// <c>additionalProperties</c>; arguments are still evaluated against the schema the host gave.
/// </summary>
internal static class ToolSchemas
{
    /// <summary>
    /// The schema a host tool definition is sent to a model with: <see cref="Wire"/>, without any
    /// <c>additionalProperties</c> keyword at any depth. A property named <c>additionalProperties</c> stays.
    /// </summary>
    internal static JsonElement Sent(JsonElement schema)
    {
        JsonNode? copy = JsonNode.Parse(Wire(schema).GetRawText());
        StructuredOutput.RemoveKeywords(copy, (keyword, _) => keyword == "additionalProperties");
        return JsonSerializer.SerializeToElement(copy);
    }

    /// <summary>The tool's own schema when its root is an object, otherwise the wrapper; arguments are evaluated against it.</summary>
    internal static JsonElement Wire(JsonElement schema) => IsObject(schema) ? schema
        : JsonElement.Parse(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["value"] = JsonNode.Parse(schema.GetRawText()) },
            ["required"] = new JsonArray("value")
        }.ToJsonString());

    /// <summary>True when the schema's root is an object schema, so it is sent unwrapped.</summary>
    internal static bool IsObject(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && (!schema.TryGetProperty("type", out JsonElement type)
            || (type.ValueKind == JsonValueKind.String && type.GetString() == "object"));

    /// <summary>
    /// Evaluates the model's arguments against the tool's schema. For a wrapped schema, the <c>value</c> member is
    /// evaluated against the tool's own schema and is the evaluation's instance; arguments without it are evaluated
    /// against the wrapper, which reports the missing member.
    /// </summary>
    internal static StructuredOutput.Evaluation EvaluateArguments(JsonElement schema, string arguments)
    {
        if (IsObject(schema)) return StructuredOutput.Evaluate(schema, arguments);
        try
        {
            using JsonDocument document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("value", out JsonElement value))
                return StructuredOutput.Evaluate(schema, value.GetRawText());
        }
        catch (JsonException) { }
        return StructuredOutput.Evaluate(Wire(schema), arguments);
    }
}
