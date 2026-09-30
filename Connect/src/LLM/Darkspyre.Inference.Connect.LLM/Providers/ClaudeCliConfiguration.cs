namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class ClaudeCliConfiguration
{
    public static LlmCredential? ValidateConnection(LlmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Transport != LlmTransport.AnthropicClaudeCli)
        {
            throw new ArgumentException(
                $"A {connection.Transport} connection cannot be used with a Claude CLI client.",
                nameof(connection));
        }

        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if (!string.IsNullOrWhiteSpace(credential?.ApiKey)
            || credential?.Aws is not null
            || credential?.SecretHeaders.Count > 0)
        {
            throw new NotSupportedException(
                "Claude CLI connections cannot contain API keys, AWS credentials, or secret headers.");
        }

        if (connection.AdditionalHeaders.Count > 0 || connection.ProviderOptions.Count > 0)
        {
            throw new NotSupportedException(
                "Claude CLI owns its configuration and does not accept connection headers or provider options.");
        }

        return credential;
    }

    public static string ResolveExecutable(string endpoint) => CliInfrastructure.ResolveExecutable(
        endpoint,
        "claude",
        "local://claude-cli",
        "Claude CLI endpoint must be 'claude', 'local://claude-cli', or an absolute executable path.");

    public static LlmProviderException Unavailable(ClaudeCliUnavailableException exception) => new(
        "Claude CLI was not found. Install the official Claude CLI, ensure claude is on PATH, "
        + "then run claude and complete sign-in.",
        LlmTransport.AnthropicClaudeCli,
        providerCode: "claude_cli_not_found",
        innerException: exception);

    public static LlmProviderException ExitTimeout(CliProcessTerminationException exception) => new(
        $"The Claude CLI did not exit after it was stopped. {exception.Message}",
        LlmTransport.AnthropicClaudeCli,
        providerCode: "claude_cli_exit_timeout",
        innerException: exception);
}
