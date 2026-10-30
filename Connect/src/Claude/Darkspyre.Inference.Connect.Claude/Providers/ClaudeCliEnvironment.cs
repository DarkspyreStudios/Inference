using System.Diagnostics;
using System.Net;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Runs a Claude CLI child as the Claude account a connection is signed in to.</summary>
internal static class ClaudeCliEnvironment
{
    internal const string OAuthTokenVariable = "CLAUDE_CODE_OAUTH_TOKEN";

    // The CLI prefers an API key or auth token to CLAUDE_CODE_OAUTH_TOKEN, and reads the token's scopes from
    // CLAUDE_CODE_OAUTH_SCOPES, so a value inherited from the parent would override or misstate the account.
    private static readonly string[] ConflictingVariables =
        ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_SCOPES"];

    /// <summary>Whether the connection holds the token of the Claude account it is signed in to.</summary>
    internal static bool HasAccountToken(LlmCredential? credential) =>
        !string.IsNullOrWhiteSpace(credential?.OAuth?.AccessToken);

    /// <summary>The token of the Claude account the connection is signed in to.</summary>
    /// <exception cref="LlmProviderException">The connection holds no account token. A Claude connection never runs
    /// as the computer's own Claude login.</exception>
    internal static string AccountToken(LlmCredential? credential) =>
        HasAccountToken(credential)
            ? credential!.OAuth!.AccessToken
            : throw new LlmProviderException(
                "This Claude connection is not signed in. Sign in to a Claude account in Settings, Providers.",
                LlmTransport.AnthropicClaudeCli, HttpStatusCode.Unauthorized, providerCode: "claude_sign_in_required");

    /// <summary>Runs the CLI child as the account of <paramref name="accountToken"/>.</summary>
    internal static void Apply(ProcessStartInfo start, string accountToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountToken);
        foreach (string name in ConflictingVariables)
        {
            start.Environment.Remove(name);
        }
        start.Environment[OAuthTokenVariable] = accountToken;
    }
}
