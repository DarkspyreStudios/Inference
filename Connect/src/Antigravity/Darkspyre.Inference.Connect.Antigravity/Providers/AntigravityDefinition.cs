using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class AntigravityDefinition
{
    public static LlmProviderDefinition Value { get; } = new()
    {
        Id = "gemini-subscription",
        DisplayName = "Gemini Subscription (Antigravity)",
        Description = "Use a Google subscription through the separately authenticated Antigravity CLI.",
        Transport = LlmTransport.GoogleAntigravityCli,
        DefaultEndpoint = "agy",
        DefaultModelId = "auto",
        DisplayOrder = 40,
        Group = SetupProviderGroup.Subscription,
        HomeFolder = ".gemini",
        EndpointKind = LlmProviderEndpointKind.Executable,
        AuthenticationCheck = LlmProviderAuthenticationCheck.ExternalTool,
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
            ProviderDefinitions.SelectModel
        ]
    };
}
