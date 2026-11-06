using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class AnthropicDefinition
{
    public static LlmProviderDefinition Value { get; } = new()
    {
        Id = "anthropic-api-key",
        DisplayName = "Anthropic API Key",
        Description = "Connect directly to the Anthropic Messages API with usage billed to an Anthropic API account.",
        Transport = LlmTransport.AnthropicMessages,
        DefaultEndpoint = "https://api.anthropic.com/v1",
        DisplayOrder = 15,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "enter-api-key",
                Title = "Enter an Anthropic API key",
                Instructions = "Create or copy an Anthropic API key, then paste it here. API usage is separate from a Claude subscription.",
                CompletionRequirement = "The API key must authenticate successfully and allow the model list to be loaded.",
                Troubleshooting = "Confirm the key belongs to an active Anthropic API account with billing enabled. A Claude subscription is not an API key."
            },
            ProviderDefinitions.SelectModel
        ]
    };
}
