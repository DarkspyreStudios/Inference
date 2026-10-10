namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The wire protocol used by a configured connection.</summary>
public enum LlmTransport
{
    OpenAiResponses = 0,
    OpenAiCompatibleChat = 1,
    AnthropicMessages = 2,
    OpenAiCodex = 3,
    AwsBedrockConverse = 4,
    GoogleAntigravityCli = 6,
    AnthropicClaudeCli = 7,
    AwsBedrockAgentCore = 8,

    /// <summary>A model the host runs in its own process; the connection's endpoint names the host's engine.</summary>
    SelfHost = 9,

    /// <summary>Apple's on-device system model through the separately installed Swift helper.</summary>
    AppleFoundationModels = 10,

    GoogleVertexAi = 11,

    /// <summary>The Gemini API (Google AI Studio) with an API key.</summary>
    GoogleGeminiApi = 12,

    XaiResponses = 13,

    /// <summary>Azure resource OpenAI Responses, with explicit deployment and authentication configuration.</summary>
    AzureOpenAiResponses = 14,

    /// <summary>Azure resource OpenAI Chat Completions, with explicit deployment and authentication configuration.</summary>
    AzureOpenAiChat = 15,

    /// <summary>Declared Copilot runtime identity; no adapter is registered by this value.</summary>
    GitHubCopilot = 16,

    /// <summary>Declared ACP runtime identity; no adapter is registered by this value.</summary>
    AgentClientProtocol = 17
}
