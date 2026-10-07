using System.Text.Json;
using Json.Pointer;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Checks schema guarantees and strict function roots without changing the schema.</summary>
internal static class XaiSchemaLimits
{
    internal static LlmStructuredOutputEnforcement? ResolveEnforcement(LlmStructuredOutputRequest? output,
        LlmProviderCapabilities capabilities) => output is null ? null : StructuredOutput.ResolveEnforcement(output,
        Fits(output.Schema) ? capabilities : capabilities with { SupportsJsonSchema = false, SupportsJsonObject = false });

    internal static void ValidateStrictTool(JsonElement schema)
    {
        if (!Fits(schema, requireObjectRoot: true))
            throw new NotSupportedException("A strict Grok tool schema is outside the supported native guarantees. The schema is not weakened or sent as non-strict.");
    }

    internal static bool Fits(JsonElement root) => Fits(root, requireObjectRoot: false);

    private static bool Fits(JsonElement root, bool requireObjectRoot)
    {
        var index = new SchemaIndex();
        if (!index.Build(root)) return false;
        var pending = new Stack<(JsonElement Schema, bool Exit)>();
        var active = new HashSet<JsonElement>();
        var done = new HashSet<JsonElement>();
        Dictionary<JsonElement, bool>? objectRoots = requireObjectRoot ? [] : null;
        pending.Push((root, false));
        while (pending.TryPop(out var next))
        {
            JsonElement schema = next.Schema;
            if (next.Exit)
            {
                if (objectRoots is not null) objectRoots[schema] = ProvesObject(schema, index, objectRoots);
                active.Remove(schema);
                done.Add(schema);
                continue;
            }
            if (done.Contains(schema) || schema.ValueKind is JsonValueKind.True or JsonValueKind.False) continue;
            if (!active.Add(schema)) return false;
            if (schema.ValueKind != JsonValueKind.Object) return false;
            SchemaLocation location = index.Locations[schema];
            if (location.Draft == SchemaDraft.Unknown) return false;
            pending.Push((schema, true));
            if (schema.TryGetProperty("$ref", out JsonElement reference))
            {
                if (!index.Resolve(location, reference, out JsonElement target)) return false;
                pending.Push((target, false));
                if (location.Draft == SchemaDraft.Draft7) continue;
            }
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
                    case "allOf":
                        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 1) return false;
                        break;
                    case "oneOf":
                        if (!DisjointTypes(value)) return false;
                        break;
                    case "items":
                        if (value.ValueKind == JsonValueKind.Array) return false;
                        break;
                    case "$dynamicRef":
                    case "$recursiveRef":
                    case "not":
                    case "if":
                    case "then":
                    case "else":
                        return false;
                }
            }
            foreach (JsonElement child in location.AppliedChildren) pending.Push((child, false));
        }
        return objectRoots is null || objectRoots.TryGetValue(root, out bool objectRoot) && objectRoot;
    }

    private static bool ProvesObject(JsonElement schema, SchemaIndex index, Dictionary<JsonElement, bool> proofs)
    {
        SchemaLocation location = index.Locations[schema];
        bool referencedObject = schema.TryGetProperty("$ref", out JsonElement reference)
            && index.Resolve(location, reference, out JsonElement target) && KnownObject(target);
        if (location.Draft == SchemaDraft.Draft7 && schema.TryGetProperty("$ref", out _)) return referencedObject;
        if (schema.TryGetProperty("type", out JsonElement type))
            return type.ValueKind == JsonValueKind.String ? TypeMask(type) == 8
                : type.ValueKind == JsonValueKind.Array && type.GetArrayLength() > 0
                    && type.EnumerateArray().All(alternative => TypeMask(alternative) == 8);
        return referencedObject || ObjectUnion("anyOf") || ObjectUnion("oneOf")
            || schema.TryGetProperty("allOf", out JsonElement intersection)
                && intersection.GetArrayLength() == 1 && KnownObject(intersection[0]);

        bool KnownObject(JsonElement candidate) => proofs.TryGetValue(candidate, out bool result) && result;

        bool ObjectUnion(string keyword) => schema.TryGetProperty(keyword, out JsonElement branches)
            && branches.GetArrayLength() > 0 && branches.EnumerateArray().All(KnownObject);
    }

    private enum SchemaDraft { Draft7, RetainSiblings, Unknown }
    private enum ResourceOrigin { Implicit, Relative, Absolute }

    private sealed record SchemaLocation(Uri BaseUri, SchemaDraft Draft, IReadOnlyList<JsonElement> AppliedChildren);
    private sealed record SchemaResource(JsonElement Schema, ResourceOrigin Origin);

    // This index describes this request's schema graph. It never fetches or registers a resource.
    private sealed class SchemaIndex
    {
        internal Dictionary<JsonElement, SchemaLocation> Locations { get; } = [];
        private readonly Dictionary<string, SchemaResource> _resources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> _anchors = new(StringComparer.Ordinal);

        internal bool Build(JsonElement root)
        {
            var initialBase = new Uri("https://json-schema.invalid/root");
            _resources.Add(initialBase.AbsoluteUri, new(root, ResourceOrigin.Implicit));
            var pending = new Stack<(JsonElement Schema, Uri BaseUri, SchemaDraft Draft, ResourceOrigin Origin)>();
            pending.Push((root, initialBase, SchemaDraft.RetainSiblings, ResourceOrigin.Implicit));
            while (pending.TryPop(out var next))
            {
                JsonElement schema = next.Schema;
                if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    Locations.Add(schema, new(next.BaseUri, next.Draft, []));
                    continue;
                }
                if (schema.ValueKind != JsonValueKind.Object) return false;
                SchemaDraft draft = next.Draft;
                if (schema.TryGetProperty("$schema", out JsonElement declaration))
                {
                    if (declaration.ValueKind != JsonValueKind.String) return false;
                    draft = declaration.GetString() switch
                    {
                        "http://json-schema.org/draft-07/schema" or "http://json-schema.org/draft-07/schema#" => SchemaDraft.Draft7,
                        "https://json-schema.org/draft/2020-12/schema" or "https://json-schema.org/draft/2020-12/schema#" => SchemaDraft.RetainSiblings,
                        _ => SchemaDraft.Unknown
                    };
                }
                Uri baseUri = next.BaseUri;
                ResourceOrigin origin = next.Origin;
                bool ignoresSiblings = draft == SchemaDraft.Draft7 && schema.TryGetProperty("$ref", out _);
                if (!ignoresSiblings && schema.TryGetProperty("$id", out JsonElement id))
                {
                    if (id.ValueKind != JsonValueKind.String || !Uri.TryCreate(baseUri, id.GetString(), out Uri? selected)
                        || selected.Fragment.Length > 1) return false;
                    baseUri = selected;
                    origin = origin == ResourceOrigin.Absolute || Uri.TryCreate(id.GetString(), UriKind.Absolute, out _)
                        ? ResourceOrigin.Absolute : ResourceOrigin.Relative;
                    string key = ResourceKey(baseUri);
                    if (_resources.TryGetValue(key, out SchemaResource? existing) && !existing.Schema.Equals(schema)) return false;
                    _resources[key] = new(schema, origin);
                }
                if (!ignoresSiblings && draft == SchemaDraft.RetainSiblings && schema.TryGetProperty("$anchor", out JsonElement anchor))
                {
                    if (anchor.ValueKind != JsonValueKind.String || anchor.GetString() is not { } name || !ValidAnchor(name)) return false;
                    string key = new UriBuilder(baseUri) { Fragment = name }.Uri.AbsoluteUri;
                    if (!_anchors.TryAdd(key, schema)) return false;
                }
                var applied = new List<JsonElement>();
                foreach (JsonProperty property in schema.EnumerateObject())
                {
                    JsonElement value = property.Value;
                    switch (property.Name)
                    {
                        case "$defs":
                        case "definitions":
                        case "properties":
                        case "patternProperties":
                        case "dependentSchemas":
                            if (value.ValueKind != JsonValueKind.Object) return false;
                            foreach (JsonProperty child in value.EnumerateObject())
                            {
                                pending.Push((child.Value, baseUri, draft, origin));
                                if (property.Name is not ("$defs" or "definitions")) applied.Add(child.Value);
                            }
                            break;
                        case "allOf":
                        case "anyOf":
                        case "oneOf":
                        case "prefixItems":
                            if (value.ValueKind != JsonValueKind.Array) return false;
                            foreach (JsonElement child in value.EnumerateArray()) Add(child);
                            break;
                        case "items" when draft == SchemaDraft.Draft7 && value.ValueKind == JsonValueKind.Array:
                            foreach (JsonElement child in value.EnumerateArray()) Add(child);
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
                            Add(value);
                            break;
                    }
                }
                Locations.Add(schema, new(baseUri, draft, applied));

                void Add(JsonElement child)
                {
                    pending.Push((child, baseUri, draft, origin));
                    applied.Add(child);
                }
            }
            return true;
        }

        internal bool Resolve(SchemaLocation location, JsonElement reference, out JsonElement target)
        {
            target = default;
            if (reference.ValueKind != JsonValueKind.String || reference.GetString() is not { } text
                || !Uri.TryCreate(location.BaseUri, text, out Uri? address)) return false;
            if (!_resources.TryGetValue(ResourceKey(address), out SchemaResource? resource)) return false;
            // The implicit URI supports relative resolution, not a caller-visible absolute identity.
            if (resource.Origin == ResourceOrigin.Implicit && text.Length != 0 && !text.StartsWith('#')
                || resource.Origin != ResourceOrigin.Absolute && Uri.TryCreate(text, UriKind.Absolute, out _)) return false;
            if (_anchors.TryGetValue(address.AbsoluteUri, out target)) return true;
            if (address.Fragment.Length == 0) target = resource.Schema;
            else if (JsonPointer.TryParse(address.Fragment, out JsonPointer pointer) && pointer.Evaluate(resource.Schema) is { } selected) target = selected;
            else return false;
            return Locations.ContainsKey(target);
        }

        private static string ResourceKey(Uri address) => new UriBuilder(address) { Fragment = string.Empty }.Uri.AbsoluteUri;

        private static bool ValidAnchor(string value) => value.Length > 0 && (char.IsAsciiLetter(value[0]) || value[0] == '_')
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static bool DisjointTypes(JsonElement branches)
    {
        if (branches.ValueKind != JsonValueKind.Array || branches.GetArrayLength() == 0) return false;
        int seen = 0;
        foreach (JsonElement branch in branches.EnumerateArray())
        {
            // Draft-07 ignores $ref siblings. A sibling type is not a proof for a referenced branch.
            if (branch.ValueKind != JsonValueKind.Object || branch.TryGetProperty("$ref", out _)
                || !branch.TryGetProperty("type", out JsonElement type)) return false;
            int mask = 0;
            if (type.ValueKind == JsonValueKind.String) mask = TypeMask(type);
            else if (type.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement alternative in type.EnumerateArray())
                {
                    int candidate = TypeMask(alternative);
                    if (candidate == 0) return false;
                    mask |= candidate;
                }
            }
            if (mask == 0 || (seen & mask) != 0) return false;
            seen |= mask;
        }
        return true;
    }

    // JSON integers also satisfy number. Those branches cannot prove oneOf exclusivity.
    private static int TypeMask(JsonElement type) => type.ValueKind != JsonValueKind.String ? 0 : type.GetString() switch
    {
        "null" => 1,
        "boolean" => 2,
        "string" => 4,
        "object" => 8,
        "array" => 16,
        "integer" => 32,
        "number" => 32 | 64,
        _ => 0
    };

    private static bool Within(JsonElement value, int maximum) => value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out decimal number) && number == decimal.Truncate(number) && number >= 0 && number <= maximum;
}
