using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// The <c>input_schema</c> Anthropic Messages accepts for a client tool. The Messages API refuses <c>oneOf</c>, <c>anyOf</c>
/// and <c>allOf</c> at a schema's top level, so a root union is sent as one flat object: every branch's properties, a
/// property the branches declare differently as a nested union of its forms, and as required the members every form
/// requires. The constraint the flat object loses is stated in the tool's description. Any other schema is sent unchanged.
/// The model's arguments are still checked against the schema the caller gave.
/// </summary>
internal static class AnthropicToolSchemas
{
    private static readonly string[] Unions = ["oneOf", "anyOf", "allOf"];

    /// <summary>The schema to send, and the sentence to add to the tool's description, or null when nothing changed.</summary>
    internal static (JsonNode Schema, string? Constraint) Sent(JsonElement schema)
    {
        JsonNode copy = JsonNode.Parse(schema.GetRawText())!;
        if (copy is not JsonObject root || Unions.FirstOrDefault(root.ContainsKey) is not { } keyword
            || root[keyword] is not JsonArray branches) return (copy, null);
        bool every = keyword == "allOf";
        root.Remove(keyword);
        JsonObject properties = root["properties"] as JsonObject ?? [];
        root.Remove("properties");
        List<string> required = [.. Names(root["required"])];
        root.Remove("required");
        // Each property's distinct forms, in the order they first appear.
        var forms = new Dictionary<string, List<JsonNode>>(StringComparer.Ordinal);
        foreach ((string name, JsonNode? form) in properties) Add(forms, name, form);
        List<List<string>> branchRequired = [];
        foreach (JsonObject branch in branches.OfType<JsonObject>())
        {
            if (branch["properties"] is JsonObject declared)
                foreach ((string name, JsonNode? form) in declared) Add(forms, name, form);
            branchRequired.Add([.. Names(branch["required"])]);
        }
        IEnumerable<string> shared = every ? branchRequired.SelectMany(names => names)
            : branchRequired.Count == 0 ? [] : branchRequired.Skip(1).Aggregate((IEnumerable<string>)branchRequired[0],
                (left, right) => left.Intersect(right, StringComparer.Ordinal));
        required.AddRange(shared.Where(name => !required.Contains(name, StringComparer.Ordinal)));
        JsonObject flat = [];
        foreach ((string name, List<JsonNode> list) in forms)
            flat[name] = list.Count == 1 ? list[0].DeepClone()
                : new JsonObject { [every ? "allOf" : "anyOf"] = new JsonArray([.. list.Select(form => form.DeepClone())]) };
        root["type"] = "object";
        root["properties"] = flat;
        if (required.Count > 0) root["required"] = new JsonArray([.. required.Select(name => JsonValue.Create(name))]);
        string? constraint = every ? null : $"Input constraint: provide the parameters of {(keyword == "oneOf" ? "exactly" : "at least")} one of: "
            + string.Join(" or ", branchRequired.Select(names => $"({string.Join(", ", names)})")) + ".";
        return (root, constraint);
    }

    private static void Add(Dictionary<string, List<JsonNode>> forms, string name, JsonNode? form)
    {
        if (form is null) return;
        if (!forms.TryGetValue(name, out List<JsonNode>? list)) forms[name] = list = [];
        if (!list.Any(known => JsonNode.DeepEquals(known, form))) list.Add(form);
    }

    private static IEnumerable<string> Names(JsonNode? required) =>
        required is JsonArray names ? names.Select(name => name?.GetValueKind() == JsonValueKind.String ? (string?)name : null)
            .OfType<string>() : [];
}
