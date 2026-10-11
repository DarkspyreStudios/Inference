namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The wire protocol used by a configured connection.</summary>
public enum LlmTransport
{
    /// <summary>The OpenAI Responses API.</summary>
    OpenAiResponses = 0,
    /// <summary>A Chat Completions endpoint compatible with OpenAI, at a configured endpoint.</summary>
    OpenAiCompatibleChat = 1,
    /// <summary>The Anthropic Messages API.</summary>
    AnthropicMessages = 2,
    /// <summary>OpenAI Codex with a ChatGPT OAuth credential.</summary>
    OpenAiCodex = 3,
    /// <summary>The AWS Bedrock Converse API.</summary>
    AwsBedrockConverse = 4,
    /// <summary>The Google Antigravity command-line tool.</summary>
    GoogleAntigravityCli = 6,
    /// <summary>The Claude command-line tool, which owns its own credential.</summary>
    AnthropicClaudeCli = 7,
    /// <summary>An AWS Bedrock AgentCore harness, named by its ARN.</summary>
    AwsBedrockAgentCore = 8,

    /// <summary>A model the host runs in its own process; the connection's endpoint names the host's engine.</summary>
    SelfHost = 9,

    /// <summary>Apple's on-device system model through the separately installed Swift helper.</summary>
    AppleFoundationModels = 10,

    /// <summary>Google Vertex AI, with a project, location and authentication mode.</summary>
    GoogleVertexAi = 11,

    /// <summary>The Gemini API (Google AI Studio) with an API key.</summary>
    GoogleGeminiApi = 12,

    /// <summary>The xAI Responses API.</summary>
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
