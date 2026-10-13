using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static class ClaudeCliConfiguration
{
    /// <summary>Arguments that keep every Claude CLI process to the tools and context the Host gives.</summary>
    internal static IReadOnlyList<string> IsolationArguments { get; } =
    [
        "--safe-mode",
        "--restricted",
        "--strict-mcp-config",
        "--disable-slash-commands",
        "--tools", string.Empty,
        "--permission-prompts", "none"
    ];

    internal static string ErrorDetail(JsonElement result, LlmCredential? credential)
    {
        var details = new List<string>();
        if (result.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement error in errors.EnumerateArray())
                if (error.ValueKind == JsonValueKind.String && error.GetString() is { } text && !string.IsNullOrWhiteSpace(text))
                    details.Add(SecretRedactor.Sanitize(text, credential));
        }
        if (details.Count == 0 && LlmJson.GetString(result, "result") is { } fallback && !string.IsNullOrWhiteSpace(fallback))
            details.Add(SecretRedactor.Sanitize(fallback, credential));
        // Individual details can contain JSON secrets. Composition uses the same redactor's display bound.
        return details.Count == 0 ? string.Empty
            : " " + SecretRedactor.Sanitize("CLI detail: " + string.Join(" ", details), credential).ReplaceLineEndings(" ").Trim();
    }

    public static LlmCredential? ValidateConnection(LlmConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        RuntimeInputAdmission.Validate(connection);
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
}
