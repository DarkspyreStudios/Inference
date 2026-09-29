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
            },
            new SetupStep
            {
                Id = "test-connection", Title = "Optional on-device test",
                Instructions = "Explicitly generate a short response on this Mac. Saving does not generate a response.",
                CompletionRequirement = "The model returns text. Apple does not report whether generation stopped naturally or reached its token limit.",
                Troubleshooting = "Recheck local model readiness and retry when no other generation is using the model."
            }
        ]
    };

    private static readonly SetupStep SelectModel = new()
    {
        Id = "select-model",
        Title = "Choose a model",
        Instructions = "Load the models available to this connection and choose the model the application should use.",
        CompletionRequirement = "A model ID must be selected before setup can continue.",
        Troubleshooting = "Reload the provider model list. If the provider does not support discovery, enter a model ID manually."
    };

    private static readonly SetupStep TestConnection = new()
    {
        Id = "test-connection",
        Title = "Optional model test",
        Instructions = "Explicitly test the selected model if desired; this can incur usage charges. Saving configuration does not run a test.",
        CompletionRequirement = "A completed response verifies the selected model. Saving without a test leaves readiness unknown.",
        Troubleshooting = "Verify the credential, account entitlement, model access, endpoint, and network connection, then retry."
    };

    public static LlmProviderDefinition SelfHost { get; } = new()
    {
        Id = "self-host",
        DisplayName = "In-process model",
        Description = "Run a model that this application loads in its own process, such as a Tensor engine.",
        Transport = LlmTransport.SelfHost,
        DefaultEndpoint = string.Empty,
        DisplayOrder = 95,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "select-engine",
                Title = "Choose an engine",
                Instructions = "Choose one of the engines this application runs. The application configures and loads each engine.",
                CompletionRequirement = "An engine must be selected.",
                Troubleshooting = "If no engine is listed, add and load one in the application's model settings."
            },
            SelectModel,
            TestConnection
        ]
    };

    public static LlmProviderDefinition OpenAiApiKey { get; } = new()
    {
        Id = "openai-api-key",
        DisplayName = "OpenAI API Key",
        Description = "Connect directly to the OpenAI API with usage billed to an OpenAI platform account.",
        Transport = LlmTransport.OpenAiResponses,
        DefaultEndpoint = "https://api.openai.com/v1",
        DisplayOrder = 10,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "enter-api-key",
                Title = "Enter an OpenAI API key",
                Instructions = "Create or copy an API key from your OpenAI platform account, then paste it here. API usage is billed separately from a ChatGPT subscription.",
                CompletionRequirement = "The API key must authenticate successfully and allow the model list to be loaded.",
                Troubleshooting = "Confirm the key belongs to an active OpenAI platform project with API billing enabled. A ChatGPT subscription alone is not an API key."
            },
            SelectModel,
            TestConnection
        ]
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
            SelectModel,
            TestConnection
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
            SelectModel,
            TestConnection
        ]
    };

    public static LlmProviderDefinition ClaudeSubscription { get; } = new()
    {
        Id = "claude-subscription",
        DisplayName = "Claude Subscription",
        Description = "Use a Claude subscription through the separately authenticated official Claude CLI.",
        Transport = LlmTransport.AnthropicClaudeCli,
        DefaultEndpoint = OperatingSystem.IsWindows() ? "claude.exe" : "claude",
        DisplayOrder = 30,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "verify-claude-cli",
                Title = "Sign in to Claude CLI",
                Instructions = "Install the official Claude CLI, ensure claude is available on PATH, then run claude in a terminal and complete sign-in with the Claude subscription used for prompts.",
                CompletionRequirement = "The claude auth status --json command must report a signed-in first-party Claude account.",
                Troubleshooting = "Run claude auth status in a terminal. If it is signed out or uses a different provider, run claude and complete Claude sign-in."
            },
            SelectModel,
            TestConnection
        ]
    };

    public static LlmProviderDefinition ChatGptSubscription { get; } = new()
    {
        Id = "chatgpt-subscription",
        DisplayName = "ChatGPT Subscription",
        Description = "Use an eligible ChatGPT subscription directly, with host-owned OAuth and managed agent execution.",
        Transport = LlmTransport.OpenAiCodex,
        DefaultEndpoint = OpenAiCodexLlmClient.DefaultEndpoint,
        DisplayOrder = 20,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "authorize-chatgpt",
                Title = "Sign in with ChatGPT",
                Instructions = "Run the sign-in action, open the displayed authorization link, and continue after entering the device code. Authorization updates this draft; Save commits it through the host.",
                CompletionRequirement = "The draft contains a ChatGPT OAuth access token and account ID before saving an authenticated connection.",
                Troubleshooting = "An existing Codex CLI login is separate and is not used. Run the ChatGPT sign-in action here."
            },
            SelectModel,
            TestConnection
        ]
    };

    public static LlmProviderDefinition GeminiSubscription { get; } = new()
    {
        Id = "gemini-subscription",
        DisplayName = "Gemini Subscription",
        Description = "Use a Google subscription through the separately authenticated Antigravity CLI.",
        Transport = LlmTransport.GoogleAntigravityCli,
        DefaultEndpoint = OperatingSystem.IsWindows() ? "agy.exe" : "agy",
        DefaultModelId = "auto",
        DisplayOrder = 40,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "verify-cli",
                Title = "Install and sign in to Antigravity",
                Instructions = "Install the official Antigravity CLI, run agy in a terminal, and complete Google sign-in. The host uses the CLI's cached authentication and does not read or store its OAuth tokens.",
                CompletionRequirement = "agy must be installed, authenticated, and able to list the models available to the Google account.",
                Troubleshooting = "Run agy directly in a terminal and complete sign-in. If the application cannot find it, enter the absolute executable path.",
                WindowsInstructions = "In PowerShell, run (Get-Command agy).Source to find the executable path.",
                MacOsInstructions = "In Terminal, run command -v agy to find the executable path.",
                LinuxInstructions = "In a terminal, run command -v agy to find the executable path."
            },
            SelectModel,
            TestConnection
        ]
    };

    public static LlmProviderDefinition GeminiApi { get; } = new()
    {
        Id = "gemini-api",
        DisplayName = "Google AI Studio (Gemini API)",
        Description = "Use Gemini models directly with a Google AI Studio API key.",
        Transport = LlmTransport.GoogleGeminiApi,
        DefaultEndpoint = GeminiApiLlmClient.DefaultEndpoint,
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
            SelectModel,
            TestConnection
        ]
    };
}
