namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class ProviderDefinitions
{
    private static readonly LlmSetupStep SelectModel = new()
    {
        Id = "select-model",
        Title = "Choose a model",
        Instructions = "Load the models available to this connection and choose the model the application should use.",
        Action = LlmSetupAction.SelectModel,
        Inputs =
        [
            new LlmSetupInput
            {
                Id = "model-id",
                DisplayName = "Model",
                HelpText = "Choose a discovered model or enter its exact provider model ID.",
                IsRequired = true
            }
        ],
        CompletionRequirement = "A model ID must be selected before setup can continue.",
        Troubleshooting = "Reload the provider model list. If the provider does not support discovery, enter a model ID manually."
    };

    private static readonly LlmSetupStep TestConnection = new()
    {
        Id = "test-connection",
        Title = "Test and finish",
        Instructions = "Test the selected model. Finish setup only after the provider accepts the connection.",
        Action = LlmSetupAction.TestConnection,
        CompletionRequirement = "The selected model must return a completed response.",
        Troubleshooting = "Verify the credential, account entitlement, model access, endpoint, and network connection, then retry."
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
            new LlmSetupStep
            {
                Id = "enter-api-key",
                Title = "Enter an OpenAI API key",
                Instructions = "Create or copy an API key from your OpenAI platform account, then paste it here. API usage is billed separately from a ChatGPT subscription.",
                Action = LlmSetupAction.EnterApiKey,
                Inputs =
                [
                    new LlmSetupInput
                    {
                        Id = "api-key",
                        DisplayName = "API key",
                        HelpText = "The key is passed to the host's protected credential store and is not saved in ordinary application settings.",
                        IsRequired = true,
                        IsSecret = true
                    }
                ],
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
            new LlmSetupStep
            {
                Id = "enter-api-key",
                Title = "Enter the API credential",
                Instructions = "Enter the credential required by the compatible endpoint. Leave it blank only when the endpoint explicitly permits unauthenticated access.",
                Action = LlmSetupAction.EnterApiKey,
                Inputs =
                [
                    new LlmSetupInput
                    {
                        Id = "api-key",
                        DisplayName = "API key",
                        IsSecret = true
                    }
                ],
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
            new LlmSetupStep
            {
                Id = "enter-api-key",
                Title = "Enter an Anthropic API key",
                Instructions = "Create or copy an Anthropic API key, then paste it here. API usage is separate from a Claude subscription.",
                Action = LlmSetupAction.EnterApiKey,
                Inputs =
                [
                    new LlmSetupInput
                    {
                        Id = "api-key",
                        DisplayName = "API key",
                        HelpText = "The key is passed to the host's protected credential store and is not saved in ordinary application settings.",
                        IsRequired = true,
                        IsSecret = true
                    }
                ],
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
            new LlmSetupStep
            {
                Id = "verify-claude-cli",
                Title = "Sign in to Claude CLI",
                Instructions = "Install the official Claude CLI, ensure claude is available on PATH, then run claude in a terminal and complete sign-in with the Claude subscription used for prompts.",
                Action = LlmSetupAction.VerifyExternalCli,
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
            new LlmSetupStep
            {
                Id = "authorize-chatgpt",
                Title = "Sign in with ChatGPT",
                Instructions = "Use LlmProviderService.SignInAsync to complete device authorization. Store the resulting OAuth credential through the host's protected ILlmConnectionSource.",
                Action = LlmSetupAction.AuthorizeDeviceCode,
                CompletionRequirement = "The host connection contains a ChatGPT OAuth access token and account ID.",
                Troubleshooting = "CLI login is separate. Run the host's ChatGPT sign-in flow; a CLI executable is not a direct endpoint."
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
            new LlmSetupStep
            {
                Id = "verify-cli",
                Title = "Install and sign in to Antigravity",
                Instructions = "Install the official Antigravity CLI, run agy in a terminal, and complete Google sign-in. The host uses the CLI's cached authentication and does not read or store its OAuth tokens.",
                Action = LlmSetupAction.VerifyExternalCli,
                Inputs =
                [
                    new LlmSetupInput
                    {
                        Id = "executable-path",
                        DisplayName = "CLI executable or path",
                        HelpText = "Use agy when it is on PATH, or enter its absolute executable path.",
                        IsRequired = true
                    }
                ],
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
}
