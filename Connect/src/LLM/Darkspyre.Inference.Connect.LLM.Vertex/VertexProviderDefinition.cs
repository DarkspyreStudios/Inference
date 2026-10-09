using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal static class VertexProviderDefinition
{
    public static LlmProviderDefinition Value { get; } = new()
    {
        Id = "google-vertex-ai",
        DisplayName = "Google Vertex AI",
        Description = "Gemini and Claude through Google Cloud with ADC or a service account; projectless Gemini express mode with an API key.",
        Transport = LlmTransport.GoogleVertexAi,
        DefaultEndpoint = VertexEndpoints.BaseEndpoint,
        DisplayOrder = 55,
        SetupSteps = Array.AsReadOnly<SetupStep>(
        [
            new SetupStep
            {
                Id = "configure-google", Title = "Google Cloud authentication and location",
                Instructions = "Select application default credentials, a protected service-account key, or a Google Cloud API key for Gemini-only express mode. ADC and service accounts require a project and explicit location. Express mode uses the global projectless endpoint and has no model listing. Global routing does not pin processing to a region.",
                CompletionRequirement = "Supply the selected authentication configuration. Catalog access does not verify model invocation permission; express mode requires an explicit inference test.",
                Troubleshooting = "Check ADC configuration or the service-account key, project, enabled API, IAM permissions, and publisher model access."
            },
            new SetupStep
            {
                Id = "select-model", Title = "Choose a publisher model",
                Instructions = "Choose a google/model text model or anthropic/claude-* model ID. Express mode requires manual Google model entry. Catalog entries have unknown invocation access until tested.",
                CompletionRequirement = "Select a supported model in the configured location.",
                Troubleshooting = "Model Garden catalog membership does not guarantee access in this project or region."
            },
            new SetupStep
            {
                Id = "test-connection", Title = "Optional metered model test",
                Instructions = "Explicitly test the selected model to verify invocation. This can incur cloud charges. Saving performs no inference.",
                CompletionRequirement = "A completed response verifies this model and connection.",
                Troubleshooting = "Check model access, region eligibility, quotas and organization policies."
            }
        ])
    };
}
