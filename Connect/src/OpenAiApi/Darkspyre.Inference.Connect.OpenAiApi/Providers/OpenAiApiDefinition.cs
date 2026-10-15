using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class OpenAiApiDefinition
{
    public static LlmProviderDefinition Value { get; } = new()
    {
        Id = "openai-api-key",
        DisplayName = "OpenAI API Key",
        Description = "Connect to the OpenAI API, or to another service that serves the same API at its own endpoint.",
        Transport = LlmTransport.OpenAiResponses,
        DefaultEndpoint = "https://api.openai.com/v1",
        DisplayOrder = 10,
        Icon = SetupIcons.FromResource(typeof(SetupIcons).Assembly, SetupIcons.OpenAiResource),
        Group = SetupProviderGroup.ApiKey,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "enter-api-key",
                Title = "Enter an API key",
                Instructions = "Paste an API key for the endpoint. An OpenAI API key comes from your OpenAI platform account and is billed separately from a ChatGPT subscription. Leave it blank for a service that takes no key, such as a local model server.",
                CompletionRequirement = "The model list loads from the endpoint.",
                Troubleshooting = "Check the endpoint and the key. A ChatGPT subscription alone is not an OpenAI API key."
            },
            ProviderDefinitions.SelectModel
        ]
    };
}
