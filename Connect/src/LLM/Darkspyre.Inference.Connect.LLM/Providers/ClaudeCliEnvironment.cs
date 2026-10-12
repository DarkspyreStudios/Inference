using System.Diagnostics;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Runs a Claude CLI child as the Claude account a connection is signed in to.</summary>
internal static class ClaudeCliEnvironment
{
    internal const string OAuthTokenVariable = "CLAUDE_CODE_OAUTH_TOKEN";

    // The CLI prefers an API key or auth token to CLAUDE_CODE_OAUTH_TOKEN, and reads the token's scopes from
    // CLAUDE_CODE_OAUTH_SCOPES, so a value inherited from the parent would override or misstate the account.
    private static readonly string[] ConflictingVariables =
        ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_SCOPES"];

    /// <summary>The token of the account the connection is signed in to, or null to run as the machine's active login.</summary>
    internal static string? AccountToken(LlmCredential? credential) => credential?.OAuth?.AccessToken;

    /// <summary>Sets the account token on a CLI child. Without a token the child keeps the inherited environment.</summary>
    internal static void Apply(ProcessStartInfo start, string? accountToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (accountToken is null)
        {
            return;
        }
        foreach (string name in ConflictingVariables)
        {
            start.Environment.Remove(name);
        }
        start.Environment[OAuthTokenVariable] = accountToken;
    }
}
