using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class GrokDefinition
{
    public static LlmProviderDefinition Value { get; } = new()
    {
        Id = "grok-subscription",
        DisplayName = "Grok Subscription",
        Description = "Use an eligible Grok subscription directly, with host-owned OAuth and managed agent execution.",
        Transport = LlmTransport.XaiResponses,
        DefaultEndpoint = new XaiConnectionOptions().GetEndpoint(),
        DefaultModelId = "grok-4.7",
        DisplayOrder = 35,
        Icon = SetupIcons.FromResource(typeof(GrokDefinition).Assembly, "Icons/grok.svg"),
        Group = SetupProviderGroup.Subscription,
        SetupSteps =
        [
            new SetupStep
            {
                Id = "authorize-grok",
                Title = "Sign in with Grok",
                Instructions = "Choose the API region, run the sign-in action, open the displayed x.ai link and approve the device code. Every request of this connection goes to the chosen region. Authorization updates this draft; Save commits it through the host. No API key is used.",
                CompletionRequirement = "The draft contains a Grok OAuth access token before saving an authenticated connection.",
                Troubleshooting = "Start sign-in again after a denied or expired code. Confirm that the account is entitled to API access.",
                ActionIds = ["authorize", "check"]
            },
            ProviderDefinitions.SelectModel
        ]
    };
}
