using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Credential shape serialized into the host's protected secret store.</summary>
public sealed record LlmCredential
{
    public string? ApiKey { get; init; }

    public LlmOAuthTokens? OAuth { get; init; }

    public LlmAwsCredentials? Aws { get; init; }

    public LlmGoogleCredentials? Google { get; init; }

    /// <summary>Protected custom headers, for providers that use credentials such as <c>x-api-key</c>.</summary>
    public IReadOnlyDictionary<string, string> SecretHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static LlmCredential? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LlmCredential>(raw, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>OAuth token fields shared by subscription-backed transports.</summary>
public sealed record LlmOAuthTokens
{
    public required string AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    public long? ExpiresAtUnixSeconds { get; init; }

    public string? IdToken { get; init; }

    public string? AccountId { get; init; }

    public string? PlanType { get; init; }

    public string? Email { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }
}

/// <summary>AWS configuration resolved by the optional Bedrock transport.</summary>
public sealed record LlmAwsCredentials
{
    public string? Region { get; init; }

    public string? Profile { get; init; }

    public string? AccessKeyId { get; init; }

    public string? SecretAccessKey { get; init; }

    public string? SessionToken { get; init; }
}
