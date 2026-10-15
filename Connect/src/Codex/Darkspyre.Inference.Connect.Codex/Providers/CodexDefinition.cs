using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class CodexDefinition
{
    public static LlmProviderDefinition Value { get; } = new()
    {
        Id = "chatgpt-subscription",
        DisplayName = "ChatGPT Subscription",
        Description = "Use an eligible ChatGPT subscription directly, with host-owned OAuth and managed agent execution.",
        Transport = LlmTransport.OpenAiCodex,
        DefaultEndpoint = OpenAiCodexLlmClient.DefaultEndpoint,
        DisplayOrder = 20,
        Icon = SetupIcons.FromResource(typeof(SetupIcons).Assembly, SetupIcons.OpenAiResource),
        Group = SetupProviderGroup.Subscription,
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
            ProviderDefinitions.SelectModel
        ]
    };
}
