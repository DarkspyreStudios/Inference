using System.Globalization;
using System.Text.Json;
using Json.Pointer;
using Json.Schema;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Checks schema guarantees and strict function roots without changing the schema.</summary>
internal static class XaiSchemaLimits
{
    internal static LlmStructuredOutputEnforcement? ResolveEnforcement(LlmStructuredOutputRequest? output,
        LlmProviderCapabilities capabilities)
    {
        if (output is null) return null;
        SchemaDisposition disposition = Analyze(output.Schema, requireObjectRoot: false);
        RefuseInvalid(disposition);
        return StructuredOutput.ResolveEnforcement(output, disposition == SchemaDisposition.Guaranteed
            ? capabilities : capabilities with { SupportsJsonSchema = false, SupportsJsonObject = false });
    }

    internal static void ValidateStrictTool(JsonElement schema)
    {
        SchemaDisposition disposition = Analyze(schema, requireObjectRoot: true);
        RefuseInvalid(disposition);
        if (disposition != SchemaDisposition.Guaranteed)
            throw new NotSupportedException("A strict Grok tool schema is outside the supported native guarantees. The schema is not weakened or sent as non-strict.");
    }

    private enum SchemaDisposition { Guaranteed, OutsideGuarantee, ProviderRejected, InvalidSchema }

    private static void RefuseInvalid(SchemaDisposition disposition)
    {
        if (disposition == SchemaDisposition.InvalidSchema)
            throw new ArgumentException("The Grok schema contains an invalid JSON Schema keyword value.");
    }

    private static SchemaDisposition Merge(SchemaDisposition left, SchemaDisposition right) => left > right ? left : right;

    private static SchemaDisposition Analyze(JsonElement root, bool requireObjectRoot)
    {
        var index = new SchemaIndex();
        if (!index.Build(root)) return index.Disposition;
        SchemaDisposition disposition = index.Disposition;
        var pending = new Stack<(JsonElement Schema, bool Exit, bool AllowsBoolean)>();
        var active = new HashSet<JsonElement>();
        var done = new HashSet<JsonElement>();
        Dictionary<JsonElement, bool>? objectRoots = requireObjectRoot ? [] : null;
        pending.Push((root, false, false));
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
            if (done.Contains(schema)) continue;
            SchemaLocation location = index.Locations[schema];
            if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                if (!next.AllowsBoolean) disposition = Merge(disposition, SchemaDisposition.OutsideGuarantee);
                continue;
            }
            if (!active.Add(schema)) { disposition = Merge(disposition, SchemaDisposition.OutsideGuarantee); continue; }
            if (location.Draft == SchemaDraft.Unknown) { disposition = Merge(disposition, SchemaDisposition.OutsideGuarantee); active.Remove(schema); continue; }
            pending.Push((schema, true, false));
            if (schema.TryGetProperty("$ref", out JsonElement reference))
            {
                if (!index.Resolve(location, reference, out JsonElement target)) disposition = Merge(disposition, SchemaDisposition.OutsideGuarantee);
                else pending.Push((target, false, false));
                if (location.Draft == SchemaDraft.Draft7) continue;
            }
            foreach (JsonProperty property in schema.EnumerateObject())
                disposition = Merge(disposition, ClassifyKeyword(property, location.Draft));
            foreach (JsonElement child in location.AppliedChildren) pending.Push((child, false, index.Locations[child].AllowsBoolean));
        }
        if (objectRoots is not null && (!objectRoots.TryGetValue(root, out bool objectRoot) || !objectRoot))
            disposition = Merge(disposition, SchemaDisposition.OutsideGuarantee);
        return disposition;
    }

    private static SchemaDisposition ClassifyKeyword(JsonProperty property, SchemaDraft draft)
    {
        if (InactiveKeyword(property.Name, draft)) return SchemaDisposition.OutsideGuarantee;
        JsonElement value = property.Value;
        switch (property.Name)
        {
            case "type":
                if (value.ValueKind == JsonValueKind.String) return Shape(TypeMask(value) != 0);
                return Shape(UniqueStrings(value, candidate => TypeMask(candidate) != 0) && value.GetArrayLength() > 0);
            case "enum":
                return value.ValueKind != JsonValueKind.Array ? SchemaDisposition.InvalidSchema
                    : value.GetArrayLength() == 0 ? SchemaDisposition.ProviderRejected : SchemaDisposition.Guaranteed;
            case "required":
                return Shape(UniqueStrings(value));
            case "format":
                return value.ValueKind != JsonValueKind.String ? SchemaDisposition.InvalidSchema
                    : value.GetString() is "date" or "time" or "date-time" or "email" or "uuid" or "ipv4" or "ipv6" or "uri"
                        ? SchemaDisposition.Guaranteed : SchemaDisposition.OutsideGuarantee;
            case "pattern":
                return ClassifyPattern(value);
            case "multipleOf":
                return value.ValueKind == JsonValueKind.Number && JsonMath.NumberCompare(value, JsonElement.Parse("0")) > 0
                    ? SchemaDisposition.OutsideGuarantee : SchemaDisposition.InvalidSchema;
            case "uniqueItems":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? SchemaDisposition.OutsideGuarantee : SchemaDisposition.InvalidSchema;
            case "dependentRequired":
                return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(dependency => UniqueStrings(dependency.Value))
                        ? SchemaDisposition.OutsideGuarantee : SchemaDisposition.InvalidSchema;
            case "$vocabulary":
                return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(vocabulary => vocabulary.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        ? SchemaDisposition.OutsideGuarantee : SchemaDisposition.InvalidSchema;
            case "minimum":
            case "maximum":
            case "exclusiveMinimum":
            case "exclusiveMaximum":
                return Shape(value.ValueKind == JsonValueKind.Number);
            case "minLength":
            case "maxLength":
                return Count(value, 2048);
            case "minItems":
            case "maxItems":
                return Count(value, 256);
            case "minProperties":
            case "maxProperties":
                return Count(value, 64);
            case "minContains":
            case "maxContains":
                return draft == SchemaDraft.Draft7 ? SchemaDisposition.ProviderRejected
                    : Count(value, int.MaxValue) == SchemaDisposition.InvalidSchema ? SchemaDisposition.InvalidSchema : SchemaDisposition.ProviderRejected;
            case "properties":
                return value.EnumerateObject().Any(child => child.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    ? SchemaDisposition.ProviderRejected : SchemaDisposition.Guaranteed;
            case "anyOf":
                return value.GetArrayLength() == 0 ? SchemaDisposition.ProviderRejected : SchemaDisposition.Guaranteed;
            case "allOf":
                return value.GetArrayLength() == 0 ? SchemaDisposition.InvalidSchema
                    : value.GetArrayLength() == 1 ? SchemaDisposition.Guaranteed : SchemaDisposition.OutsideGuarantee;
            case "oneOf":
                return value.GetArrayLength() == 0 ? SchemaDisposition.InvalidSchema
                    : DisjointTypes(value) ? SchemaDisposition.Guaranteed : SchemaDisposition.OutsideGuarantee;
            case "items":
                return value.ValueKind == JsonValueKind.Array ? SchemaDisposition.ProviderRejected : SchemaDisposition.Guaranteed;
            case "$schema":
            case "$id":
            case "$anchor":
            case "$ref":
            case "$comment":
            case "title":
            case "description":
            case "contentEncoding":
            case "contentMediaType":
                return Shape(value.ValueKind == JsonValueKind.String);
            case "readOnly":
            case "writeOnly":
            case "deprecated":
                return Shape(value.ValueKind is JsonValueKind.True or JsonValueKind.False);
            case "examples":
                return Shape(value.ValueKind == JsonValueKind.Array);
            case "const":
            case "default":
            case "$defs":
            case "definitions":
            case "additionalProperties":
            case "prefixItems":
                return SchemaDisposition.Guaranteed;
            case "$dynamicRef":
            case "$recursiveRef":
            case "not":
            case "if":
            case "then":
            case "else":
                return SchemaDisposition.OutsideGuarantee;
            default:
                return SchemaDisposition.OutsideGuarantee;
        }

        static SchemaDisposition Shape(bool valid) => valid ? SchemaDisposition.Guaranteed : SchemaDisposition.InvalidSchema;
    }

    // This pass classifies known support differences. It does not parse or match ECMA expressions.
    private static SchemaDisposition ClassifyPattern(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return SchemaDisposition.InvalidSchema;
        string pattern = value.GetString()!;
        SchemaDisposition disposition = SchemaDisposition.Guaranteed;
        bool characterClass = false;
        int groupDepth = 0;
        for (int index = 0; index < pattern.Length; index++)
        {
            char atom = pattern[index];
            if (atom == '\\')
            {
                if (++index == pattern.Length) return SchemaDisposition.OutsideGuarantee;
                char escaped = pattern[index];
                bool property = escaped is 'p' or 'P' && index + 1 < pattern.Length && pattern[index + 1] == '{';
                bool namedReference = escaped == 'k' && index + 1 < pattern.Length && pattern[index + 1] == '<';
                if (property || !characterClass && (escaped is >= '1' and <= '9' or 'b' or 'B' || namedReference))
                    disposition = Merge(disposition, SchemaDisposition.ProviderRejected);
                else if (char.IsAsciiLetterOrDigit(escaped) && escaped is not ('n' or 't' or 'r' or 'f' or 'x' or 'u'
                    or 'd' or 'D' or 'w' or 'W' or 's' or 'S'))
                    disposition = Merge(disposition, SchemaDisposition.OutsideGuarantee);
                continue;
            }
            if (atom == '[') characterClass = true;
            else if (atom == ']') characterClass = false;
            else if (!characterClass && atom == '.')
                disposition = Merge(disposition, SchemaDisposition.OutsideGuarantee);
            else if (!characterClass && atom == '(')
            {
                groupDepth++;
                if (index + 2 < pattern.Length && pattern[index + 1] == '?')
                {
                    char introducer = pattern[index + 2];
                    bool lookbehind = introducer == '<' && index + 3 < pattern.Length && pattern[index + 3] is '=' or '!';
                    if (introducer is '=' or '!' or 'i' or 'm' or 's' or '-' || lookbehind)
                        disposition = Merge(disposition, SchemaDisposition.ProviderRejected);
                    else if (introducer != ':') disposition = Merge(disposition, SchemaDisposition.OutsideGuarantee);
                }
            }
            else if (!characterClass && atom == ')')
            {
                if (groupDepth == 0) return SchemaDisposition.InvalidSchema;
                groupDepth--;
            }
        }
        if (groupDepth != 0) return SchemaDisposition.InvalidSchema;
        return characterClass ? Merge(disposition, SchemaDisposition.OutsideGuarantee) : disposition;
    }

    private static SchemaDisposition Count(JsonElement value, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Number) return SchemaDisposition.InvalidSchema;
        if (!value.TryGetDecimal(out decimal number)) return SchemaDisposition.OutsideGuarantee;
        if (number < 0 || number != decimal.Truncate(number)) return SchemaDisposition.InvalidSchema;
        JsonElement parsed = JsonElement.Parse(number.ToString(CultureInfo.InvariantCulture));
        if (JsonMath.NumberCompare(value, parsed) != 0) return SchemaDisposition.InvalidSchema;
        return number <= maximum ? SchemaDisposition.Guaranteed : SchemaDisposition.OutsideGuarantee;
    }

    private static bool UniqueStrings(JsonElement array, Func<JsonElement, bool>? supported = null)
    {
        if (array.ValueKind != JsonValueKind.Array) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement value in array.EnumerateArray())
            if (value.ValueKind != JsonValueKind.String || supported is not null && !supported(value) || !seen.Add(value.GetString()!)) return false;
        return true;
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

    private enum SchemaDraft { Draft7, Draft202012, RetainSiblings, Unknown }
    private enum ResourceOrigin { Implicit, Relative, Absolute }

    private static bool InactiveKeyword(string name, SchemaDraft draft) => draft switch
    {
        SchemaDraft.Draft7 => name is "prefixItems" or "dependentSchemas" or "dependentRequired"
            or "unevaluatedItems" or "unevaluatedProperties" or "$anchor" or "$dynamicAnchor"
            or "$dynamicRef" or "$vocabulary" or "deprecated",
        SchemaDraft.Draft202012 => name is "additionalItems" or "dependencies",
        _ => false
    };

    private sealed record SchemaLocation(Uri BaseUri, SchemaDraft Draft, IReadOnlyList<JsonElement> AppliedChildren, bool AllowsBoolean = false);
    private sealed record SchemaResource(JsonElement Schema, ResourceOrigin Origin);

    // This index describes this request's schema graph. It never fetches or registers a resource.
    private sealed class SchemaIndex
    {
        internal Dictionary<JsonElement, SchemaLocation> Locations { get; } = [];
        internal SchemaDisposition Disposition { get; private set; } = SchemaDisposition.Guaranteed;
        private readonly Dictionary<string, SchemaResource> _resources = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JsonElement> _anchors = new(StringComparer.Ordinal);

        internal bool Build(JsonElement root)
        {
            var initialBase = new Uri("https://json-schema.invalid/root");
            _resources.Add(initialBase.AbsoluteUri, new(root, ResourceOrigin.Implicit));
            var pending = new Stack<(JsonElement Schema, Uri BaseUri, SchemaDraft Draft, ResourceOrigin Origin, bool AllowsBoolean)>();
            pending.Push((root, initialBase, SchemaDraft.RetainSiblings, ResourceOrigin.Implicit, false));
            while (pending.TryPop(out var next))
            {
                JsonElement schema = next.Schema;
                if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    Locations.Add(schema, new(next.BaseUri, next.Draft, [], next.AllowsBoolean));
                    continue;
                }
                if (schema.ValueKind != JsonValueKind.Object) return Reject(SchemaDisposition.InvalidSchema);
                SchemaDraft draft = next.Draft;
                if (schema.TryGetProperty("$schema", out JsonElement declaration))
                {
                    if (declaration.ValueKind != JsonValueKind.String) return Reject(SchemaDisposition.InvalidSchema);
                    draft = declaration.GetString() switch
                    {
                        "http://json-schema.org/draft-07/schema" or "http://json-schema.org/draft-07/schema#" => SchemaDraft.Draft7,
                        "https://json-schema.org/draft/2020-12/schema" or "https://json-schema.org/draft/2020-12/schema#" => SchemaDraft.Draft202012,
                        _ => SchemaDraft.Unknown
                    };
                }
                if (draft == SchemaDraft.Unknown)
                {
                    Locations.Add(schema, new(next.BaseUri, draft, []));
                    continue;
                }
                Uri baseUri = next.BaseUri;
                ResourceOrigin origin = next.Origin;
                bool ignoresSiblings = draft == SchemaDraft.Draft7 && schema.TryGetProperty("$ref", out _);
                if (!ignoresSiblings && schema.TryGetProperty("$id", out JsonElement id))
                {
                    if (id.ValueKind != JsonValueKind.String || !Uri.TryCreate(baseUri, id.GetString(), out Uri? selected))
                        return Reject(SchemaDisposition.InvalidSchema);
                    if (selected.Fragment.Length > 1) Disposition = SchemaDisposition.OutsideGuarantee;
                    baseUri = selected;
                    origin = origin == ResourceOrigin.Absolute || Uri.TryCreate(id.GetString(), UriKind.Absolute, out _)
                        ? ResourceOrigin.Absolute : ResourceOrigin.Relative;
                    string key = ResourceKey(baseUri);
                    if (_resources.TryGetValue(key, out SchemaResource? existing) && !existing.Schema.Equals(schema))
                        Disposition = SchemaDisposition.OutsideGuarantee;
                    else _resources[key] = new(schema, origin);
                }
                if (!ignoresSiblings && draft is SchemaDraft.Draft202012 or SchemaDraft.RetainSiblings
                    && schema.TryGetProperty("$anchor", out JsonElement anchor))
                {
                    if (anchor.ValueKind != JsonValueKind.String || anchor.GetString() is not { } name || !ValidAnchor(name))
                        return Reject(SchemaDisposition.InvalidSchema);
                    string key = new UriBuilder(baseUri) { Fragment = name }.Uri.AbsoluteUri;
                    if (!_anchors.TryAdd(key, schema)) Disposition = SchemaDisposition.OutsideGuarantee;
                }
                var applied = new List<JsonElement>();
                foreach (JsonProperty property in schema.EnumerateObject())
                {
                    JsonElement value = property.Value;
                    if (InactiveKeyword(property.Name, draft)) continue;
                    if (ignoresSiblings && property.Name is not ("$ref" or "$defs" or "definitions")) continue;
                    switch (property.Name)
                    {
                        case "dependencies" when draft == SchemaDraft.Draft7:
                            if (value.ValueKind != JsonValueKind.Object) return Reject(SchemaDisposition.InvalidSchema);
                            foreach (JsonProperty dependency in value.EnumerateObject())
                            {
                                if (dependency.Value.ValueKind == JsonValueKind.Array)
                                {
                                    if (!UniqueStrings(dependency.Value)) return Reject(SchemaDisposition.InvalidSchema);
                                }
                                else Add(dependency.Value);
                            }
                            break;
                        case "$defs":
                        case "definitions":
                        case "properties":
                        case "patternProperties":
                        case "dependentSchemas":
                            if (value.ValueKind != JsonValueKind.Object) return Reject(SchemaDisposition.InvalidSchema);
                            foreach (JsonProperty child in value.EnumerateObject())
                            {
                                pending.Push((child.Value, baseUri, draft, origin, false));
                                if (property.Name is not ("$defs" or "definitions")) applied.Add(child.Value);
                            }
                            break;
                        case "allOf":
                        case "anyOf":
                        case "oneOf":
                        case "prefixItems":
                            if (value.ValueKind != JsonValueKind.Array) return Reject(SchemaDisposition.InvalidSchema);
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
                            Add(value, property.Name == "additionalProperties");
                            break;
                    }
                    if ((!ignoresSiblings || property.Name == "$ref") && ClassifyKeyword(property, draft) == SchemaDisposition.InvalidSchema)
                        return Reject(SchemaDisposition.InvalidSchema);
                }
                Locations.Add(schema, new(baseUri, draft, applied));

                void Add(JsonElement child, bool allowsBoolean = false)
                {
                    pending.Push((child, baseUri, draft, origin, allowsBoolean));
                    applied.Add(child);
                }
            }
            return true;
        }

        private bool Reject(SchemaDisposition failure)
        {
            Disposition = failure;
            return false;
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
}
