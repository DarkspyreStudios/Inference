using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Credential shape serialized into the host's protected secret store.</summary>
public sealed record LlmCredential
{
    /// <summary>Returns a fixed placeholder so secrets never reach logs.</summary>
    public override string ToString() => "LlmCredential { [redacted] }";

    /// <summary>The provider API key, or null.</summary>
    public string? ApiKey { get; init; }

    /// <summary>OAuth tokens for a subscription sign-in, or null.</summary>
    public LlmOAuthTokens? OAuth { get; init; }

    /// <summary>AWS credentials for Bedrock, or null.</summary>
    public LlmAwsCredentials? Aws { get; init; }

    /// <summary>Google credentials, or null.</summary>
    public LlmGoogleCredentials? Google { get; init; }

    /// <summary>The Azure secret, or null.</summary>
    public LlmAzureSecret? Azure { get; init; }

    /// <summary>GitHub Copilot credentials, or null.</summary>
    public LlmCopilotCredentials? Copilot { get; init; }

    /// <summary>The credential for a Copilot runtime connection, or null.</summary>
    public LlmCopilotRuntimeConnectionCredential? CopilotRuntime { get; init; }

    /// <summary>Protected custom headers, for providers that use credentials such as <c>x-api-key</c>.</summary>
    public IReadOnlyDictionary<string, string> SecretHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serializes the credential to JSON for the protected secret store.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, ConnectLlmJson.Credential);

    /// <summary>Parses a credential from JSON. Returns null when the text is blank or malformed.</summary>
    public static LlmCredential? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(raw, ConnectLlmJson.Credential);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>OAuth token fields shared by subscription-backed transports.</summary>
public sealed record LlmOAuthTokens
{
    /// <summary>Returns a fixed placeholder so tokens never reach logs.</summary>
    public override string ToString() => "LlmOAuthTokens { [redacted] }";

    /// <summary>The access token sent with requests.</summary>
    public required string AccessToken { get; init; }

    /// <summary>The token used to obtain a new access token, or null.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>When the access token expires, in Unix seconds, or null when unknown.</summary>
    public long? ExpiresAtUnixSeconds { get; init; }

    /// <summary>The OpenID Connect identity token, or null.</summary>
    public string? IdToken { get; init; }

    /// <summary>The provider account identifier, or null.</summary>
    public string? AccountId { get; init; }

    /// <summary>The subscription plan type, or null.</summary>
    public string? PlanType { get; init; }

    /// <summary>The account email address, or null.</summary>
    public string? Email { get; init; }

    /// <summary>The scopes granted to the tokens, or null when unknown.</summary>
    public IReadOnlyList<string>? Scopes { get; init; }
}

/// <summary>AWS configuration resolved by the optional Bedrock transport.</summary>
public sealed record LlmAwsCredentials
{
    /// <summary>The AWS region, or null to resolve it from the environment or profile.</summary>
    public string? Region { get; init; }

    /// <summary>The AWS shared configuration profile name, or null.</summary>
    public string? Profile { get; init; }

    /// <summary>The AWS access key identifier, or null.</summary>
    public string? AccessKeyId { get; init; }

    /// <summary>The AWS secret access key, or null.</summary>
    public string? SecretAccessKey { get; init; }

    /// <summary>The AWS session token for temporary credentials, or null.</summary>
    public string? SessionToken { get; init; }
}
