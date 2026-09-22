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
    AwsBedrockAgentCore = 8
}
