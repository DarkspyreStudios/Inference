using System.Text.Json;
using Json.Pointer;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Detects proven local ordinary-$ref cycles without validating or fetching schemas.</summary>
internal static class AnthropicProgrammaticSchemas
{
    private static readonly Uri Dialect = new("https://json-schema.org/draft/2020-12/schema");

    internal static void Validate(JsonElement schema)
    {
        var index = new Index(schema);
        if (index.HasCycle())
            throw new NotSupportedException("Programmatic Claude client tools do not support recursive local $ref input schemas. Direct-only tools retain their schemas.");
    }

    private sealed class Node(JsonElement schema, JsonPointer path, Uri? resource)
    {
        internal JsonElement Schema { get; } = schema;
        internal JsonPointer Path { get; } = path;
        internal Uri? Resource { get; } = resource;
        internal List<Node> Children { get; } = [];
    }

    private sealed class Index
    {
        private readonly Dictionary<JsonPointer, Node> nodes = [];
        private readonly Dictionary<Uri, Node?> resources = [];
        private readonly Dictionary<(Uri Resource, string Anchor), Node?> anchors = [];

        internal Index(JsonElement schema)
        {
            var pending = new Stack<(JsonElement Schema, JsonPointer Path, Node? Parent, bool Applies, Uri? Resource)>();
            // This per-request identity is never a locator, registry entry or network request.
            pending.Push((schema, JsonPointer.Empty, null, false, new Uri($"https://inference.invalid/schema/{Guid.NewGuid():N}")));
            while (pending.TryPop(out var entry))
            {
                if (!IsSchema(entry.Schema)) continue;
                Uri? resource = entry.Resource;
                bool declaredResource = entry.Parent is null;
                bool known = true;
                if (entry.Schema.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    known = entry.Schema.EnumerateObject().All(property => names.Add(property.Name));
                    if (entry.Schema.TryGetProperty("$schema", out JsonElement dialect))
                        known &= dialect.ValueKind == JsonValueKind.String && Uri.TryCreate(dialect.GetString(), UriKind.Absolute, out Uri? declared)
                            && declared == Dialect && declared.GetComponents(UriComponents.Fragment, UriFormat.UriEscaped).Length == 0;
                    if (entry.Schema.TryGetProperty("$id", out JsonElement id))
                    {
                        declaredResource = true;
                        resource = id.ValueKind == JsonValueKind.String && Resolve(resource, id.GetString()!, out Uri? identity)
                            && identity.GetComponents(UriComponents.Fragment, UriFormat.UriEscaped).Length == 0
                            ? new UriBuilder(identity) { Fragment = "" }.Uri : null;
                    }
                }
                var node = new Node(entry.Schema, entry.Path, known ? resource : null);
                nodes.Add(entry.Path, node);
                if (entry.Applies) entry.Parent!.Children.Add(node);
                if (!known) continue;
                if (resource is not null)
                {
                    if (declaredResource) Register(resources, resource, node);
                    AddAnchor(node, "$anchor");
                    // Ordinary $ref can use this static anchor; dynamic evaluation is not performed.
                    AddAnchor(node, "$dynamicAnchor");
                }
                if (entry.Schema.ValueKind != JsonValueKind.Object) continue;
                foreach (JsonProperty keyword in entry.Schema.EnumerateObject())
                {
                    JsonPointer path = entry.Path.Combine(keyword.Name);
                    switch (keyword.Name)
                    {
                        case "$defs":
                        case "definitions":
                        case "properties":
                        case "patternProperties":
                        case "dependentSchemas":
                        case "dependencies":
                            if (keyword.Value.ValueKind != JsonValueKind.Object) break;
                            var keys = new HashSet<string>(StringComparer.Ordinal);
                            if (!keyword.Value.EnumerateObject().All(property => keys.Add(property.Name))) break;
                            foreach (JsonProperty child in keyword.Value.EnumerateObject())
                                pending.Push((child.Value, path.Combine(child.Name), node,
                                    keyword.Name is not ("$defs" or "definitions"), resource));
                            break;
                        case "allOf":
                        case "anyOf":
                        case "oneOf":
                        case "prefixItems":
                            if (keyword.Value.ValueKind != JsonValueKind.Array) break;
                            int position = 0;
                            foreach (JsonElement child in keyword.Value.EnumerateArray())
                                pending.Push((child, path.Combine(position++), node, true, resource));
                            break;
                        case "additionalProperties":
                        case "items":
                        case "contains":
                        case "propertyNames":
                        case "not":
                        case "if":
                        case "then":
                        case "else":
                        case "unevaluatedProperties":
                        case "unevaluatedItems":
                        case "contentSchema":
                            pending.Push((keyword.Value, path, node, true, resource));
                            break;
                    }
                }
            }
        }

        internal bool HasCycle()
        {
            var active = new HashSet<Node>();
            var completed = new HashSet<Node>();
            var pending = new Stack<(Node Node, bool Exit)>();
            foreach (Node node in nodes.Values)
            {
                if (completed.Contains(node) || !KnownResource(node)) continue;
                pending.Push((node, false));
                while (pending.TryPop(out var entry))
                {
                    if (entry.Exit) { active.Remove(entry.Node); completed.Add(entry.Node); continue; }
                    if (completed.Contains(entry.Node) || !KnownResource(entry.Node)) continue;
                    if (!active.Add(entry.Node)) return true;
                    pending.Push((entry.Node, true));
                    if (Reference(entry.Node) is { } target) pending.Push((target, false));
                    foreach (Node child in entry.Node.Children) pending.Push((child, false));
                }
            }
            return false;
        }

        private bool KnownResource(Node node) => node.Resource is { } resource
            && resources.TryGetValue(resource, out Node? root) && root is not null;

        private Node? Reference(Node node)
        {
            if (node.Schema.ValueKind != JsonValueKind.Object || !node.Schema.TryGetProperty("$ref", out JsonElement value)
                || value.ValueKind != JsonValueKind.String || !Resolve(node.Resource, value.GetString()!, out Uri? reference)) return null;
            string fragment = reference.GetComponents(UriComponents.Fragment, UriFormat.UriEscaped);
            var identity = new UriBuilder(reference) { Fragment = "" }.Uri;
            if (!resources.TryGetValue(identity, out Node? root) || root is null) return null;
            // URI decoding preserves literal '+'. The pointer parser owns '~0' and '~1'.
            string decoded = Uri.UnescapeDataString(fragment);
            Node? target;
            if (JsonPointer.TryParse(decoded, out JsonPointer pointer))
                nodes.TryGetValue(root.Path.Combine(pointer), out target);
            else anchors.TryGetValue((identity, decoded), out target);
            return target is not null && KnownResource(target) ? target : null;
        }

        private void AddAnchor(Node node, string keyword)
        {
            if (node.Schema.ValueKind != JsonValueKind.Object || !node.Schema.TryGetProperty(keyword, out JsonElement value)
                || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } anchor
                || !AnchorStart(anchor[0]) || anchor.Skip(1).Any(character => !AnchorStart(character)
                    && character is not (>= '0' and <= '9' or '-' or '.'))) return;
            Register(anchors, (node.Resource!, anchor), node);
        }

        // Ambiguous identities remain provider validation, not evidence of a local cycle.
        private static void Register<TKey>(Dictionary<TKey, Node?> table, TKey key, Node node) where TKey : notnull
        { if (!table.TryAdd(key, node)) table[key] = null; }

        private static bool AnchorStart(char character) => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
    }

    private static bool IsSchema(JsonElement value) => value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False;

    private static bool Resolve(Uri? resource, string reference, out Uri identity)
    {
        identity = null!;
        return resource is null ? Uri.TryCreate(reference, UriKind.Absolute, out identity!)
            : Uri.TryCreate(resource, reference, out identity!);
    }
}
