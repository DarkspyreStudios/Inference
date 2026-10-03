using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Chat Completions adapter for explicitly configured OpenAI-compatible endpoints.</summary>
internal sealed class OpenAiCompatibleChatLlmClient(HttpClient http) : ChatCompletionsLlmClientBase(http)
{
    public override LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;

    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiCompatible;

    protected override LlmProviderCapabilities DefaultCapabilities => base.DefaultCapabilities with
    {
        SupportsAgentSessions = true,
        SupportsAgentToolUse = true,
        SupportsAgentTurnInstructions = true,
        Agent = ManagedAgentPolicy.Capabilities with { RichContentSupported = base.DefaultCapabilities.RichContentSupported }
    };
}
