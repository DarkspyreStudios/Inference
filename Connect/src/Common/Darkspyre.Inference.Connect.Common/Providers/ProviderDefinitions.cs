using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class ProviderDefinitions
{
    public static LlmProviderDefinition AppleFoundationModels { get; } = new()
    {
        Id = "apple-foundation-models",
        DisplayName = "Apple Foundation Models (On Device)",
        Description = "Use Apple Intelligence's on-device language model on Apple Silicon.",
        Transport = LlmTransport.AppleFoundationModels,
        DefaultEndpoint = "darkspyre-apple-fm",
        DefaultModelId = "apple-system-default",
        DisplayOrder = 50,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "verify-apple", Title = "Prepare the on-device model",
                Instructions = "Use an Apple Silicon Mac running macOS 26 or later. Enable Apple Intelligence in System Settings and wait for its model download. Install the Darkspyre Apple helper and enter its executable name or absolute path. No API key or account sign-in is required.",
                CompletionRequirement = "The helper and on-device model must report that they are available.",
                Troubleshooting = "Check the helper path, Apple Intelligence eligibility, its enabled setting and model download status."
            },
            new SetupStep
            {
                Id = "select-model", Title = "Choose the on-device model",
                Instructions = "Load the locally available Apple system model.",
                CompletionRequirement = "Select apple-system-default.",
                Troubleshooting = "Recheck the helper and Apple Intelligence readiness."
            }
        ]
    };

    internal static readonly SetupStep SelectModel = new()
    {
        Id = "select-model",
        Title = "Choose a model",
        Instructions = "Load the models available to this connection and choose the model the application should use.",
        CompletionRequirement = "A model ID must be selected before setup can continue.",
        Troubleshooting = "Reload the provider model list. If the provider does not support discovery, enter a model ID manually."
    };



    public static LlmProviderDefinition OpenAiCompatible { get; } = new()
    {
        Id = "openai-compatible-api",
        DisplayName = "OpenAI-Compatible API",
        Description = "Connect to a service that implements the OpenAI Chat Completions protocol.",
        Transport = LlmTransport.OpenAiCompatibleChat,
        DefaultEndpoint = "http://localhost:11434/v1",
        IsGuidedSetupOption = false,
        DisplayOrder = 90,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "enter-api-key",
                Title = "Enter the API credential",
                Instructions = "Enter the credential required by the compatible endpoint. Leave it blank only when the endpoint explicitly permits unauthenticated access.",
                CompletionRequirement = "The configured endpoint must accept the credential or explicitly allow unauthenticated requests.",
                Troubleshooting = "Confirm the endpoint implements OpenAI-compatible Chat Completions and that its model-list route is enabled."
            },
            SelectModel
        ]
    };

    public static LlmProviderDefinition AnthropicApiKey { get; } = new()
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
            SelectModel
        ]
    };



    public static LlmProviderDefinition GrokSubscription { get; } = new()
    {
        Id = "grok-subscription",
        DisplayName = "Grok (SuperGrok / X Premium)",
        Description = "Connect directly to xAI with a host-owned Grok OAuth credential.",
        Transport = LlmTransport.XaiResponses,
        DefaultEndpoint = new XaiConnectionOptions().GetEndpoint(),
        DefaultModelId = "grok-4.7",
        DisplayOrder = 35,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "authorize-grok",
                Title = "Sign in with Grok",
                Instructions = "Run sign-in and approve the displayed device code in your browser. Choose the API region and service tier. Authorization updates this draft; Save commits it through the host. No API key or Grok CLI login is used.",
                CompletionRequirement = "Complete Grok OAuth sign-in before saving an authenticated connection.",
                Troubleshooting = "Restart an expired or denied authorization. Confirm that the account permits the selected model and API feature."
            },
            SelectModel
        ]
    };

    public static LlmProviderDefinition GeminiApi { get; } = new()
    {
        Id = "gemini-api",
        DisplayName = "Google AI Studio (Gemini API)",
        Description = "Use Gemini models directly with a Google AI Studio API key.",
        Transport = LlmTransport.GoogleGeminiApi,
        DefaultEndpoint = "https://generativelanguage.googleapis.com",
        DefaultModelId = "gemini-3-flash-preview",
        DisplayOrder = 54,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "enter-gemini-key",
                Title = "Enter a Gemini API key",
                Instructions = "Create an API key in Google AI Studio at https://aistudio.google.com/app/apikey and enter it here. The key is stored only by the host's protected credential store.",
                CompletionRequirement = "An API key is required. Loading the model list confirms that Google accepts the key.",
                Troubleshooting = "Check that the key is active, that the Generative Language API is enabled for its project, and that the project's billing tier allows the selected model."
            },
            SelectModel
        ]
    };
}
