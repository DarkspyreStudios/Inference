using System.Globalization;
using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Providers;

// Private typed generation plan. Standard schemas are interpreted once here; Swift constructs
// public DynamicGenerationSchema values from the plan, never Apple's private Codable JSON.
internal sealed record AppleSchemaNode(string Kind)
{
    public string? Description { get; init; }
    public IReadOnlyList<AppleSchemaProperty>? Properties { get; init; }
    public AppleSchemaNode? Items { get; init; }
    public IReadOnlyList<string>? Choices { get; init; }
    public string? Minimum { get; init; }
    public string? Maximum { get; init; }
    public int? MinItems { get; init; }
    public int? MaxItems { get; init; }
}

internal sealed record AppleSchemaProperty(string Name, bool Required, AppleSchemaNode Schema);

internal static class AppleSchema
{
    private static readonly HashSet<string> Annotations = ["title", "description", "$schema", "$defs", "$comment"];

    internal static AppleSchemaNode Convert(JsonElement schema)
    {
        int remaining = 16_384;
        return Read(schema, schema, [], 0, ref remaining);
    }

    private static AppleSchemaNode Read(JsonElement schema, JsonElement root, HashSet<string> visiting, int depth, ref int remaining)
    {
        if (--remaining < 0 || depth > 64 || schema.ValueKind != JsonValueKind.Object)
            throw Unsupported("Schemas must be objects with depth at most 64 and at most 16,384 expanded nodes.");
        JsonProperty[] members = schema.EnumerateObject().ToArray();
        if (members.Select(static p => p.Name).Distinct(StringComparer.Ordinal).Count() != members.Length)
            throw Unsupported("Duplicate schema keywords are not supported.");
        string? guidance = Guidance(schema);
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            if (members.Any(p => p.Name != "$ref" && !Annotations.Contains(p.Name)))
                throw Unsupported("Validation siblings of $ref are not supported.");
            string value = reference.ValueKind == JsonValueKind.String ? reference.GetString()! : string.Empty;
            if (!value.StartsWith("#/$defs/", StringComparison.Ordinal) || !visiting.Add(value))
                throw Unsupported("Only acyclic local $defs references are supported.");
            JsonElement target = root;
            foreach (string encoded in value[2..].Split('/'))
            {
                string name = encoded.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty(name, out target))
                    throw Unsupported("A schema reference could not be resolved.");
            }
            AppleSchemaNode result = Read(target, root, visiting, depth + 1, ref remaining);
            visiting.Remove(value);
            return guidance is null ? result : result with { Description = guidance };
        }
        if (!schema.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String)
            throw Unsupported("Each schema requires one explicit supported type.");
        string kind = type.GetString()!;
        string[] allowed = kind switch
        {
            "object" => ["properties", "required", "additionalProperties"],
            "array" => ["items", "minItems", "maxItems"],
            "string" => ["enum", "const"],
            "integer" or "number" => ["minimum", "maximum"],
            "boolean" => [],
            _ => throw Unsupported("The schema type is unsupported.")
        };
        if (members.Any(p => p.Name != "type" && !Annotations.Contains(p.Name) && !allowed.Contains(p.Name, StringComparer.Ordinal)))
            throw Unsupported("The schema contains an unsupported validation keyword.");
        var node = new AppleSchemaNode(kind)
        { Description = guidance };
        switch (kind)
        {
            case "object":
                if (!schema.TryGetProperty("additionalProperties", out JsonElement additional) || additional.ValueKind != JsonValueKind.False)
                    throw Unsupported("Objects must specify additionalProperties:false.");
                JsonElement properties = schema.TryGetProperty("properties", out JsonElement propertyValue) ? propertyValue : default;
                if (properties.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
                    throw Unsupported("Object properties must be an object.");
                var required = new HashSet<string>(StringComparer.Ordinal);
                if (schema.TryGetProperty("required", out JsonElement requiredValue))
                {
                    if (requiredValue.ValueKind != JsonValueKind.Array) throw Unsupported("required must be an array.");
                    foreach (JsonElement name in requiredValue.EnumerateArray())
                        if (!required.Add(String(name))) throw Unsupported("Duplicate required properties are invalid.");
                }
                var fields = new List<AppleSchemaProperty>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (properties.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty property in properties.EnumerateObject())
                    {
                        if (!names.Add(property.Name)) throw Unsupported("Duplicate object properties are invalid.");
                        fields.Add(new(property.Name, required.Contains(property.Name), Read(property.Value, root, visiting, depth + 1, ref remaining)));
                    }
                if (!required.IsSubsetOf(names)) throw Unsupported("Required properties must be declared.");
                return node with { Properties = fields };
            case "array":
                if (!schema.TryGetProperty("items", out JsonElement items)) throw Unsupported("Arrays require one item schema.");
                int? minimum = Count(schema, "minItems"), maximum = Count(schema, "maxItems");
                if (minimum > maximum) throw Unsupported("Array count bounds are reversed.");
                return node with { Items = Read(items, root, visiting, depth + 1, ref remaining), MinItems = minimum, MaxItems = maximum };
            case "string":
                if (schema.TryGetProperty("enum", out JsonElement choices))
                {
                    if (schema.TryGetProperty("const", out _) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                        throw Unsupported("Use one nonempty string enum or a string const.");
                    string[] values = choices.EnumerateArray().Select(String).ToArray();
                    if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw Unsupported("Enum choices must be unique.");
                    return node with { Choices = values };
                }
                return schema.TryGetProperty("const", out JsonElement constant) ? node with { Choices = [String(constant)] } : node;
            case "integer":
            case "number":
                string? low = Bound(schema, "minimum", kind), high = Bound(schema, "maximum", kind);
                if (low is not null && high is not null && (kind == "integer"
                    ? long.Parse(low, CultureInfo.InvariantCulture) > long.Parse(high, CultureInfo.InvariantCulture)
                    : double.Parse(low, CultureInfo.InvariantCulture) > double.Parse(high, CultureInfo.InvariantCulture)))
                    throw Unsupported("Numeric bounds are reversed.");
                return node with { Minimum = low, Maximum = high };
            default: return node;
        }
    }

    private static string? Guidance(JsonElement schema)
    {
        string? title = schema.TryGetProperty("title", out JsonElement name) ? String(name) : null;
        string? description = schema.TryGetProperty("description", out JsonElement text) ? String(text) : null;
        return title is null ? description : description is null ? title : $"{title}: {description}";
    }

    // Native primitive/array nodes have no description constructor. Carry their annotations as
    // path-qualified guidance as well, without claiming annotations impose validation constraints.
    internal static string Guidance(AppleSchemaNode node, string path = "$")
    {
        var lines = new List<string>();
        if (node.Description is { } description) lines.Add($"{path}: {description}");
        foreach (AppleSchemaProperty property in node.Properties ?? [])
            lines.Add(Guidance(property.Schema, $"{path}[{JsonSerializer.Serialize(property.Name)}]"));
        if (node.Items is { } items) lines.Add(Guidance(items, $"{path}[]"));
        return string.Join("\n", lines.Where(static line => line.Length > 0));
    }

    private static string? Bound(JsonElement schema, string name, string kind)
    {
        if (!schema.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.Number) throw Unsupported("Numeric bounds must be numbers.");
        if (kind == "integer")
            return value.TryGetInt64(out long integer) ? integer.ToString(CultureInfo.InvariantCulture)
                : throw Unsupported("Integer bounds must fit signed 64-bit integers.");
        return value.TryGetDouble(out double number) && double.IsFinite(number)
            ? number.ToString("R", CultureInfo.InvariantCulture) : throw Unsupported("Numeric bounds must be finite doubles.");
    }

    private static int? Count(JsonElement schema, string name)
    {
        if (!schema.TryGetProperty(name, out JsonElement value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int count) && count >= 0
            ? count : throw Unsupported("Array bounds must be nonnegative 32-bit integers.");
    }

    private static string String(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw Unsupported("A schema string was expected.");

    private static NotSupportedException Unsupported(string message) => new($"Apple schema: {message}");
}
