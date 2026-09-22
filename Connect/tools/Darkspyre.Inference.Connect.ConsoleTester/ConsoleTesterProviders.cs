using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.ConsoleTester;

internal sealed record ConsoleTesterProvider(
    string Id,
    string DisplayName,
    string ConnectionId,
    LlmTransport Transport,
    string Endpoint)
{
    public LlmConnection CreateConnection(string version, string? credential) => new()
    {
        Version = version,
        Transport = Transport,
        Endpoint = Endpoint,
        Credential = credential
    };
}

internal static class ConsoleTesterProviders
{
    private static readonly ConsoleTesterProvider Codex = new(
        "codex",
        "ChatGPT Codex subscription",
        "console-tester-codex",
        LlmTransport.OpenAiCodex,
        "https://chatgpt.com/backend-api/codex");

    private static readonly ConsoleTesterProvider Claude = new(
        "claude",
        "Claude subscription",
        "console-tester-claude",
        LlmTransport.AnthropicClaudeCli,
        OperatingSystem.IsWindows() ? "claude.exe" : "claude");

    private static readonly ConsoleTesterProvider Antigravity = new(
        "antigravity",
        "Google Antigravity subscription",
        "console-tester-antigravity",
        LlmTransport.GoogleAntigravityCli,
        OperatingSystem.IsWindows() ? "agy.exe" : "agy");

    private static readonly ConsoleTesterProvider Bedrock = new(
        "bedrock",
        "Amazon Bedrock",
        "console-tester-bedrock",
        LlmTransport.AwsBedrockConverse,
        "https://bedrock-runtime.amazonaws.com");

    private static readonly ConsoleTesterProvider AgentCore = new(
        "agentcore",
        "Amazon Bedrock AgentCore Harness",
        "console-tester-agentcore",
        LlmTransport.AwsBedrockAgentCore,
        "arn:aws:bedrock-agentcore:REGION:ACCOUNT_ID:harness/HARNESS_ID");

    public static IReadOnlyList<ConsoleTesterProvider> All { get; } =
        [Codex, Claude, Antigravity, Bedrock, AgentCore];

    public static ConsoleTesterProvider Resolve(string id) => id.ToLowerInvariant() switch
    {
        "codex" or "chatgpt" or "chatgpt-subscription" => Codex,
        "claude" or "claude-subscription" => Claude,
        "antigravity" or "agy" or "gemini-subscription" => Antigravity,
        "bedrock" or "aws-bedrock" => Bedrock,
        "agentcore" or "bedrock-agentcore" => AgentCore,
        _ => throw new ArgumentException(
            $"Unknown provider '{id}'. Choose one of: {string.Join(", ", All.Select(provider => provider.Id))}.",
            nameof(id))
    };

    public static ConsoleTesterProvider? FindByConnectionId(string connectionId) =>
        All.FirstOrDefault(provider => string.Equals(
            provider.ConnectionId,
            connectionId,
            StringComparison.Ordinal));
}
