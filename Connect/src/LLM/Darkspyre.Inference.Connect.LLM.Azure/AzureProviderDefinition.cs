using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Azure;

internal static class AzureProviderDefinition
{
    internal static LlmProviderDefinition Responses { get; } = Create(LlmTransport.AzureOpenAiResponses, "azure-openai-responses", "Azure OpenAI / Foundry Responses", 56);
    internal static LlmProviderDefinition Chat { get; } = Create(LlmTransport.AzureOpenAiChat, "azure-openai-chat", "Azure OpenAI / Foundry Chat Completions", 57);

    private static LlmProviderDefinition Create(LlmTransport transport, string id, string name, int order) => new()
    {
        Id = id,
        DisplayName = name,
        Transport = transport,
        DisplayOrder = order,
        DefaultEndpoint = string.Empty,
        Description = "Azure resource OpenAI v1 with an explicitly configured deployment and protected resource API key.",
        SetupSteps =
        [
            new SetupStep
            {
                Id = "configure-azure", Title = "Azure resource and API key",
                Instructions = "Configure the resource and selected authentication. Only OpenAI v1 with a protected resource API key is available; Entra and versioned routes cannot be saved or invoked.",
                CompletionRequirement = "Supply an authority without an API path, query or fragment.",
                Troubleshooting = "Verify the resource endpoint, API-key access policy and network permissions."
            },
            new SetupStep
            {
                Id = "select-deployment", Title = "Azure deployment",
                Instructions = "Enter the exact deployment name. An optional underlying model profile supplies validation, not model access.",
                CompletionRequirement = "Supply the deployment routing identity.",
                Troubleshooting = "Deployment names can differ from public model IDs. Discovery is not required."
            }
        ]
    };
}
