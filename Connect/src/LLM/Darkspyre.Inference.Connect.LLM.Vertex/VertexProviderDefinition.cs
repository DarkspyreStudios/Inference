using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal static class VertexProviderDefinition
{
    public static LlmProviderDefinition Value { get; } = new()
    {
        Id = "google-vertex-ai",
        DisplayName = "Google Vertex AI",
        Description = "Gemini and Claude through Google Cloud, using application default credentials or a service account.",
        Transport = LlmTransport.GoogleVertexAi,
        DefaultEndpoint = VertexEndpoints.BaseEndpoint,
        DisplayOrder = 55,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "configure-google", Title = "Google Cloud authentication and location",
                Instructions = "Enable the Vertex AI API and grant the chosen principal model invocation access. Select application default credentials configured outside this application, or supply a protected Google service-account key. Enter the project and an explicit location; global routing has different residency semantics from a region.",
                CompletionRequirement = "Resolve the configured Google credential and load the publisher catalog. Catalog access does not verify model invocation permission.",
                Troubleshooting = "Check ADC configuration or the service-account key, project, enabled API, IAM permissions, and publisher model access."
            },
            new SetupStep
            {
                Id = "select-model", Title = "Choose a publisher model",
                Instructions = "Choose a qualified google/model or anthropic/model ID. Catalog entries have unknown invocation access until tested.",
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
        ]
    };
}
