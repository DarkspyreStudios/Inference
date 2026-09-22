namespace Darkspyre.Inference.Connect.LLM.Examples;

/// <summary>Named entry points showing the correct tool lane for every supported provider.</summary>
public static class ProviderToolExamples
{
    private const string Prompt =
        "Call get_weather for Chicago, then answer with the returned temperature and conditions.";

    /// <summary>OpenAI API-key connection using the Responses chat tool lane.</summary>
    public static Task<LlmChatResult> OpenAiResponsesAsync(
        LlmProviderService providers,
        string connectionId,
        string modelId,
        CancellationToken cancellationToken = default) =>
        ChatToolLane.RunAsync(
            providers,
            connectionId,
            modelId,
            Prompt,
            cancellationToken);

    /// <summary>OpenAI-compatible endpoint using the Chat Completions tool lane.</summary>
    public static Task<LlmChatResult> OpenAiCompatibleChatAsync(
        LlmProviderService providers,
        string connectionId,
        string modelId,
        CancellationToken cancellationToken = default) =>
        ChatToolLane.RunAsync(
            providers,
            connectionId,
            modelId,
            Prompt,
            cancellationToken);

    /// <summary>Anthropic API-key connection using native Messages tool blocks.</summary>
    public static Task<LlmChatResult> AnthropicApiKeyAsync(
        LlmProviderService providers,
        string connectionId,
        string modelId,
        CancellationToken cancellationToken = default) =>
        ChatToolLane.RunAsync(
            providers,
            connectionId,
            modelId,
            Prompt,
            cancellationToken);

    /// <summary>AWS profile connection using native Bedrock Converse tool blocks.</summary>
    public static Task<LlmChatResult> BedrockConverseAsync(
        LlmProviderService providers,
        string connectionId,
        string modelId,
        CancellationToken cancellationToken = default) =>
        ChatToolLane.RunAsync(
            providers,
            connectionId,
            modelId,
            Prompt,
            cancellationToken);

    /// <summary>ChatGPT subscription using Darkspyre.Inference.Connect.LLM's self-owned managed agent loop.</summary>
    public static Task<AgentToolExampleResult> CodexSubscriptionAsync(
        LlmAgentProviderService agents,
        string connectionId,
        string modelId,
        string workingDirectory,
        CancellationToken cancellationToken = default) =>
        AgentToolLane.RunAsync(
            agents,
            connectionId,
            modelId,
            workingDirectory,
            Prompt,
            true,
            cancellationToken);

    /// <summary>Direct ChatGPT subscription with an explicitly host-owned chat tool loop.</summary>
    public static Task<LlmChatResult> CodexSubscriptionChatAsync(
        LlmProviderService providers,
        string connectionId,
        string modelId,
        CancellationToken cancellationToken = default) =>
        ChatToolLane.RunAsync(providers, connectionId, modelId, Prompt, cancellationToken);

    /// <summary>Claude subscription using the Claude CLI SDK/MCP agent tool lane.</summary>
    public static Task<AgentToolExampleResult> ClaudeSubscriptionAsync(
        LlmAgentProviderService agents,
        string connectionId,
        string modelId,
        string workingDirectory,
        CancellationToken cancellationToken = default) =>
        AgentToolLane.RunAsync(
            agents,
            connectionId,
            modelId,
            workingDirectory,
            Prompt,
            true,
            cancellationToken);

    /// <summary>AWS profile connection using an AgentCore Harness inline-function tool.</summary>
    public static Task<AgentToolExampleResult> BedrockAgentCoreAsync(
        LlmAgentProviderService agents,
        string connectionId,
        string modelId,
        string workingDirectory,
        CancellationToken cancellationToken = default) =>
        AgentToolLane.RunAsync(
            agents,
            connectionId,
            modelId,
            workingDirectory,
            Prompt,
            false,
            cancellationToken);
}
