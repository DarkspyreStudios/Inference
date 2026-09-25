using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Json.Schema;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class StructuredOutput
{
    public static LlmStructuredOutputEnforcement ResolveEnforcement(
        LlmStructuredOutputRequest request,
        LlmProviderCapabilities capabilities)
    {
        if (capabilities.SupportsJsonSchema)
        {
            return LlmStructuredOutputEnforcement.ProviderJsonSchema;
        }

        if (request.Fallback >= LlmStructuredOutputFallback.JsonObject
            && capabilities.SupportsJsonObject)
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
            JsonSchema schema = JsonSchema.FromText(request.Schema.GetRawText());
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
