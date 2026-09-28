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

    GoogleVertexAi = 11
}
