using Darkspyre.Inference.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Json.Schema;
using Json.Pointer;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class StructuredOutput
{
    // The keywords whose value is one schema, or an array of schemas, besides the name-keyed ones.
    private static readonly string[] SubschemaKeywords = ["items", "prefixItems", "oneOf", "anyOf", "allOf", "not", "if", "then",
        "else", "contains", "propertyNames", "additionalProperties", "unevaluatedItems", "unevaluatedProperties"];

    // The keywords whose value is an object of schemas keyed by a name, which is never itself a keyword.
    private static readonly string[] NamedSubschemaKeywords = ["properties", "patternProperties", "dependentSchemas", "$defs", "definitions"];

    /// <summary>
    /// Removes each keyword <paramref name="remove"/> selects from <paramref name="node"/> and every schema inside it. A
    /// property named like a keyword stays.
    /// </summary>
    internal static void RemoveKeywords(JsonNode? node, Func<string, JsonNode?, bool> remove)
    {
        if (node is not JsonObject schema) return;
        foreach (string keyword in schema.Select(pair => pair.Key).Where(key => remove(key, schema[key])).ToArray()) schema.Remove(keyword);
        foreach (string keyword in SubschemaKeywords)
        {
            if (schema[keyword] is JsonArray branches) foreach (JsonNode? branch in branches) RemoveKeywords(branch, remove);
            else RemoveKeywords(schema[keyword], remove);
        }
        foreach (string keyword in NamedSubschemaKeywords)
            if (schema[keyword] is JsonObject named) foreach ((string _, JsonNode? value) in named) RemoveKeywords(value, remove);
    }

    // A keyword that refuses every member a schema does not declare.
    private static bool RefusesUndeclaredMembers(string keyword, JsonNode? value) =>
        keyword is "additionalProperties" or "unevaluatedProperties" && value?.GetValueKind() == JsonValueKind.False;

    /// <summary>
    /// How the structured answer is enforced. Never throws: a schema whose root is not an object, or a route
    /// without a JSON format, is checked locally.
    /// </summary>
    public static LlmStructuredOutputEnforcement ResolveEnforcement(
        LlmStructuredOutputRequest request,
        LlmProviderCapabilities capabilities)
    {
        // A JSON format would force an object answer, so another root is checked locally only.
        if (!HasObjectRoot(request.Schema))
        {
            return LlmStructuredOutputEnforcement.LocalValidationOnly;
        }

        if (capabilities.SupportsJsonSchema != InferenceSupport.Unsupported)
        {
            return LlmStructuredOutputEnforcement.ProviderJsonSchema;
        }

        return capabilities.SupportsJsonObject != InferenceSupport.Unsupported
            ? LlmStructuredOutputEnforcement.ProviderJsonObject
            : LlmStructuredOutputEnforcement.LocalValidationOnly;
    }

    private static bool HasObjectRoot(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("type", out JsonElement type)
        && type.ValueKind == JsonValueKind.String
        && type.GetString() == "object";

    /// <summary>The instruction that asks for an answer satisfying <paramref name="schema"/>, followed by the schema.</summary>
    internal static string Guidance(JsonElement schema) =>
        "Return the final answer as exactly one JSON value that satisfies this JSON Schema, with no Markdown fence or other text.\n"
        + schema.GetRawText();

    /// <summary>The result of checking a JSON text against a schema.</summary>
    /// <param name="Checked">False when the schema could not be built or evaluated.</param>
    /// <param name="Errors">One line per failed check; superfluous members are not errors.</param>
    /// <param name="Instance">The parsed value, unchanged; default when the text is not JSON.</param>
    /// <param name="Failures">Schema-owned identifying facts for the failed checks.</param>
    internal sealed record Evaluation(bool Checked, IReadOnlyList<string> Errors, JsonElement Instance,
        IReadOnlyList<LlmStructuredOutputFailure> Failures);

    /// <summary>
    /// Checks <paramref name="json"/> against <paramref name="schema"/>. Nothing is downloaded, and members the schema
    /// does not declare are ignored at every depth: an <c>additionalProperties</c> or <c>unevaluatedProperties</c> of
    /// <c>false</c> is removed before the check, so a <c>oneOf</c> branch still matches a value with extra members.
    /// </summary>
    internal static Evaluation Evaluate(JsonElement schema, string json)
    {
        JsonElement instance;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            instance = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            return new(true, [$"not valid JSON: {exception.Message}"], default, []);
        }

        JsonSchema built;
        JsonNode? tolerant;
        try
        {
            tolerant = JsonNode.Parse(schema.GetRawText());
            RemoveKeywords(tolerant, RefusesUndeclaredMembers);
            built = JsonSchema.FromText(tolerant?.ToJsonString() ?? schema.GetRawText(), new BuildOptions
            {
                SchemaRegistry = new SchemaRegistry
                {
                    Fetch = (uri, _) => throw new JsonSchemaException($"Schema '{uri}' is not available locally.")
                },
                Dialect = BuildOptions.Default.Dialect,
                DialectRegistry = BuildOptions.Default.DialectRegistry,
                VocabularyRegistry = BuildOptions.Default.VocabularyRegistry
            });
        }
        catch (Exception exception) when (exception is JsonException or JsonSchemaException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            return new(false, [$"could not be checked: {exception.Message}"], instance, []);
        }

        try
        {
            EvaluationResults results = built.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
            // The whole schema decides. A passing schema can still list errors from alternatives that did not match.
            if (results.IsValid) return new(true, [], instance, []);
            var rootBase = new Uri(results.SchemaLocation.GetLeftPart(UriPartial.Query));
            var errors = new List<string>();
            var failures = new List<LlmStructuredOutputFailure>();
            foreach (EvaluationResults detail in (results.Details ?? []).Prepend(results))
            {
                if (detail.Errors is null) continue;
                bool report = !detail.IsValid;
                for (EvaluationResults? parent = detail.Parent; report && parent is not null; parent = parent.Parent)
                    report = !parent.IsValid;
                foreach ((string keyword, string message) in detail.Errors)
                {
                    string location = detail.InstanceLocation.ToString();
                    errors.Add($"{(location.Length == 0 ? "/" : location)}: {message}");
                    if (!report) continue;
                    IReadOnlyList<string> names = SchemaNames.Of(keyword, detail, tolerant!, rootBase, instance);
                    if (!failures.Any(failure => failure.Keyword == keyword && failure.PropertyNames.SequenceEqual(names, StringComparer.Ordinal)))
                        failures.Add(new(keyword, names));
                }
            }
            return new(true, errors, instance, failures);
        }
        catch (Exception exception) when (exception is JsonException or JsonSchemaException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            return new(false, [$"could not be checked: {exception.Message}"], instance, []);
        }
    }

    public static LlmStructuredOutputValidation Validate(
        string text,
        LlmStructuredOutputRequest request,
        LlmStructuredOutputEnforcement enforcement)
    {
        Evaluation evaluation = Evaluate(request.Schema, text);
        return new(enforcement, evaluation.Checked, evaluation.Checked && evaluation.Errors.Count == 0, evaluation.Errors)
        { Failures = evaluation.Failures };
    }
}

/// <summary>Reads failed property names only from the evaluated schema's own paths and declarations.</summary>
internal static class SchemaNames
{
    /// <summary>The schema-owned names one failed keyword of this evaluation node involves.</summary>
    internal static IReadOnlyList<string> Of(string keyword, EvaluationResults node, JsonNode built, Uri rootBase,
        JsonElement instance)
    {
        if (keyword is "additionalProperties" or "unevaluatedProperties" or "propertyNames" or "patternProperties"
            or "dependentSchemas" or "if" or "then" or "else" or "allOf" or "anyOf" or "oneOf" or "not") return [];
        if (keyword is "required" or "dependentRequired")
        {
            // A fragment in an embedded $id resource does not address the root document that was built.
            if (new Uri(node.SchemaLocation.GetLeftPart(UriPartial.Query)) != rootBase
                || !JsonPointer.TryParse(node.SchemaLocation.Fragment, out JsonPointer pointer)
                || !pointer.TryEvaluate(built, out JsonNode? schema) || schema is not JsonObject declaration) return [];
            JsonElement? value = node.InstanceLocation.Evaluate(instance);
            if (value is not { ValueKind: JsonValueKind.Object }) return [];
            JsonElement current = value.Value;
            if (keyword == "required")
                return declaration["required"] is JsonArray required ? Missing(required, current) : [];
            var missing = new List<string>();
            if (declaration["dependentRequired"] is JsonObject dependencies)
                foreach ((string trigger, JsonNode? names) in dependencies)
                    if (current.TryGetProperty(trigger, out _) && names is JsonArray required)
                        missing.AddRange(Missing(required, current));
            return [.. missing.Distinct(StringComparer.Ordinal)];
        }

        JsonPointer path = node.EvaluationPath;
        string? property = null;
        for (int index = 0; index < path.SegmentCount; index++)
        {
            string segment = path[index].ToString();
            switch (segment)
            {
                case "properties":
                    if (++index >= path.SegmentCount) return [];
                    property = path[index].ToString().Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                    break;
                case "$defs" or "definitions" or "dependentSchemas" or "patternProperties":
                    if (++index >= path.SegmentCount) return [];
                    break;
                case "allOf" or "anyOf" or "oneOf" or "prefixItems":
                    if (++index >= path.SegmentCount || path[index].ToInt() is not >= 0) return [];
                    break;
                case "items" or "additionalProperties" or "unevaluatedProperties" or "if" or "then" or "else" or "not" or "contains" or "$ref":
                    break;
                default:
                    if (index != path.SegmentCount - 1 || segment != keyword) return [];
                    break;
            }
        }
        return property is null ? [] : [property];
    }

    private static IReadOnlyList<string> Missing(JsonArray required, JsonElement current) =>
        [.. required.Select(name => name!.GetValue<string>()).Where(name => !current.TryGetProperty(name, out _))];
}
