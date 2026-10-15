using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class ClaudeDefinition
{
    public static LlmProviderDefinition Value { get; } = new()
    {
        Id = "claude-subscription",
        DisplayName = "Claude Subscription",
        Description = "Use a Claude subscription through the separately authenticated official Claude CLI.",
        Transport = LlmTransport.AnthropicClaudeCli,
        DefaultEndpoint = "claude",
        DisplayOrder = 30,
        Icon = SetupIcons.FromResource(typeof(ClaudeDefinition).Assembly, "Icons/claude.svg"),
        Group = SetupProviderGroup.Subscription,
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
            ProviderDefinitions.SelectModel
        ]
    };
}
