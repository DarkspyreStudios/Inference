using Darkspyre.Inference.Common;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Json.Schema;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class StructuredOutput
{
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
    internal sealed record Evaluation(bool Checked, IReadOnlyList<string> Errors, JsonElement Instance);

    /// <summary>
    /// Checks <paramref name="json"/> against <paramref name="schema"/>. Nothing is downloaded, and members the schema
    /// does not declare are ignored.
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
            return new(true, [$"not valid JSON: {exception.Message}"], default);
        }

        JsonSchema built;
        try
        {
            built = JsonSchema.FromText(schema.GetRawText(), new BuildOptions
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
            return new(false, [$"could not be checked: {exception.Message}"], instance);
        }

        try
        {
            EvaluationResults results = built.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List });
            // The whole schema decides. A passing schema can still list errors from alternatives that did not match.
            if (results.IsValid) return new(true, [], instance);
            var errors = new List<string>();
            foreach (EvaluationResults detail in (results.Details ?? []).Prepend(results))
            {
                if (detail.Errors is null) continue;
                string applied = detail.EvaluationPath.ToString();
                bool superfluousMember = applied.EndsWith("/additionalProperties", StringComparison.Ordinal)
                    || applied.EndsWith("/unevaluatedProperties", StringComparison.Ordinal);
                foreach ((string keyword, string message) in detail.Errors)
                {
                    // These keywords, and the false schema they apply to each extra member, mark a superfluous
                    // member, and superfluous members are ignored.
                    if (keyword is "additionalProperties" or "unevaluatedProperties" || (superfluousMember && keyword.Length == 0)) continue;
                    string location = detail.InstanceLocation.ToString();
                    errors.Add($"{(location.Length == 0 ? "/" : location)}: {message}");
                }
            }
            return new(true, errors, instance);
        }
        catch (Exception exception) when (exception is JsonException or JsonSchemaException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            return new(false, [$"could not be checked: {exception.Message}"], instance);
        }
    }

    public static LlmStructuredOutputValidation Validate(
        string text,
        LlmStructuredOutputRequest request,
        LlmStructuredOutputEnforcement enforcement)
    {
        Evaluation evaluation = Evaluate(request.Schema, text);
        return new(enforcement, evaluation.Checked, evaluation.Checked && evaluation.Errors.Count == 0, evaluation.Errors);
    }
}
