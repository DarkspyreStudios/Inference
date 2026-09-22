namespace Darkspyre.Inference.Connect.LLM.Examples;

/// <summary>
/// Representative connection records for each tool-capable lane. Credential JSON is loaded by the
/// host's protected <see cref="ILlmConnectionSource"/>; these examples never contain raw secrets.
/// </summary>
public static class ConnectionExamples
{
    public static LlmConnection OpenAiResponses(
        string version,
        string protectedApiKeyCredentialJson) => new()
        {
            Version = version,
            Transport = LlmTransport.OpenAiResponses,
            Endpoint = "https://api.openai.com/v1",
            Credential = protectedApiKeyCredentialJson
        };

    public static LlmConnection OpenAiCompatibleChat(
        string version,
        string endpoint,
        string protectedCredentialJson) => new()
        {
            Version = version,
            Transport = LlmTransport.OpenAiCompatibleChat,
            Endpoint = endpoint,
            Credential = protectedCredentialJson
        };

    public static LlmConnection AnthropicApiKey(
        string version,
        string protectedApiKeyCredentialJson) => new()
        {
            Version = version,
            Transport = LlmTransport.AnthropicMessages,
            Endpoint = "https://api.anthropic.com/v1",
            Credential = protectedApiKeyCredentialJson
        };

    public static LlmConnection Bedrock(
        string version,
        string protectedAwsConfigurationJson) => new()
        {
            Version = version,
            Transport = LlmTransport.AwsBedrockConverse,
            Endpoint = "https://bedrock-runtime.amazonaws.com",
            Credential = protectedAwsConfigurationJson
        };

    public static LlmConnection BedrockAgentCore(
        string version,
        string harnessArn,
        string protectedAwsConfigurationJson) => new()
        {
            Version = version,
            Transport = LlmTransport.AwsBedrockAgentCore,
            Endpoint = harnessArn,
            Credential = protectedAwsConfigurationJson
        };

    /// <summary>Direct ChatGPT subscription. The host owns device-OAuth credentials, not CLI tokens.</summary>
    public static LlmConnection CodexSubscription(
        string version,
        string protectedOAuthCredentialJson) => new()
        {
            Version = version,
            Transport = LlmTransport.OpenAiCodex,
            Endpoint = "https://chatgpt.com/backend-api/codex",
            Credential = protectedOAuthCredentialJson
        };

    /// <summary>The official Claude CLI owns its login. No vault credential is supplied.</summary>
    public static LlmConnection ClaudeSubscription(
        string version,
        string executable = "claude") => new()
        {
            Version = version,
            Transport = LlmTransport.AnthropicClaudeCli,
            Endpoint = executable,
            Credential = null
        };

    /// <summary>The official Antigravity CLI owns its login. It has no host tool lane.</summary>
    public static LlmConnection AntigravitySubscription(
        string version,
        string executable = "agy") => new()
        {
            Version = version,
            Transport = LlmTransport.GoogleAntigravityCli,
            Endpoint = executable,
            Credential = null
        };
}
