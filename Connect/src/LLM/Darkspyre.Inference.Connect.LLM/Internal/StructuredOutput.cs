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

    public static LlmStructuredOutputValidation Validate(
        string text,
        LlmStructuredOutputRequest request,
        LlmStructuredOutputEnforcement enforcement)
    {
        try
        {
            using JsonDocument instanceDocument = JsonDocument.Parse(text);
            JsonSchema schema = JsonSchema.FromText(request.Schema.GetRawText(), new BuildOptions
            {
                SchemaRegistry = new SchemaRegistry(),
                Dialect = BuildOptions.Default.Dialect,
                DialectRegistry = BuildOptions.Default.DialectRegistry,
                VocabularyRegistry = BuildOptions.Default.VocabularyRegistry
            });
            EvaluationResults results = schema.Evaluate(instanceDocument.RootElement);
            return results.IsValid
                ? new LlmStructuredOutputValidation(enforcement, true, true, [])
                : Invalid(enforcement, "The response JSON does not satisfy the requested schema.");
        }
        catch (JsonException exception)
        {
            return Invalid(enforcement, $"The response is not valid JSON: {exception.Message}");
        }
        catch (JsonSchemaException exception)
        {
            return new(enforcement, false, false,
                [$"The response schema could not be evaluated locally: {exception.Message}"]);
        }
    }

    private static LlmStructuredOutputValidation Invalid(
        LlmStructuredOutputEnforcement enforcement,
        string message) => new(enforcement, true, false, [message]);
}
