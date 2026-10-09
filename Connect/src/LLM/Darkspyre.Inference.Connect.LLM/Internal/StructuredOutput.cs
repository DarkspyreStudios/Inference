using Darkspyre.Inference.Common;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Json.Schema;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class StructuredOutput
{
    public static void ValidateRequest(LlmChatRequest request)
    {
        if (request.StructuredOutput is { } structured
            && (string.IsNullOrWhiteSpace(structured.Name)
                || structured.Schema.ValueKind != JsonValueKind.Object))
        {
            throw new ArgumentException(
                "Structured output requires a name and a JSON object schema.", nameof(request));
        }
    }

    public static LlmStructuredOutputEnforcement ResolveEnforcement(
        LlmStructuredOutputRequest request,
        LlmProviderCapabilities capabilities)
    {
        if ((capabilities.SupportsJsonSchema != InferenceSupport.Unsupported))
        {
            return LlmStructuredOutputEnforcement.ProviderJsonSchema;
        }

        if (request.Fallback >= LlmStructuredOutputFallback.JsonObject
            && (capabilities.SupportsJsonObject != InferenceSupport.Unsupported))
        {
            return LlmStructuredOutputEnforcement.ProviderJsonObject;
        }

        if (request.Fallback == LlmStructuredOutputFallback.LocalValidation)
        {
            return LlmStructuredOutputEnforcement.LocalValidationOnly;
        }

        throw new NotSupportedException(
            "The selected connection cannot enforce JSON Schema and the request does not permit an available fallback.");
    }

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
    }

    private static LlmStructuredOutputValidation Invalid(
        LlmStructuredOutputEnforcement enforcement,
        string message) => new(enforcement, true, false, [message]);
}
