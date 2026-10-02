using System.Net;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM;

public enum LlmGoogleAuthenticationMode
{
    ApplicationDefault,
    ServiceAccount,
    /// <summary>Global projectless Vertex express mode with a Google Cloud API key. Gemini only; no model listing.</summary>
    ApiKey
}

/// <summary>Non-secret Google Cloud connection selection. Credentials remain in LlmCredential.</summary>
public sealed record LlmVertexConfiguration
{
    public string? ProjectId { get; init; }
    public string? Location { get; init; }
    public LlmGoogleAuthenticationMode AuthenticationMode { get; init; }
    public LlmVertexCacheStaging? CacheStaging { get; init; }
}

/// <summary>Caller-managed derived cache staging. The caller controls permissions and unchanged-object retention.</summary>
public sealed record LlmVertexCacheStaging(string Bucket, string ObjectPrefix)
{
    [JsonRequired]
    public string Bucket { get; init => field = ValidateBucket(value); } = ValidateBucket(Bucket);
    [JsonRequired]
    public string ObjectPrefix { get; init => field = ValidatePrefix(value); } = ValidatePrefix(ObjectPrefix);
    public override string ToString() => "LlmVertexCacheStaging { <configuration> }";

    private static string ValidateBucket(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 3 or > 222 || !char.IsAsciiLetterOrDigit(value[0]) || !char.IsAsciiLetterOrDigit(value[^1])
            || value.Any(character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '_' or '-'))
            || value.Split('.').Any(component => component.Length is < 1 or > 63) || IPAddress.TryParse(value, out _))
            throw new ArgumentException("Use a Cloud Storage bucket name, not a URL or address.", nameof(value));
        return value;
    }

    private static string ValidatePrefix(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(char.IsControl)) throw new ArgumentException("Object prefixes cannot contain controls.", nameof(value));
        if (value.IndexOfAny(['#', '?']) >= 0)
            throw new ArgumentException("The local unqualified gs:// representation cannot preserve this prefix without ambiguity.", nameof(value));
        return value;
    }
}

/// <summary>Host-protected service-account key JSON. ADC requires no stored key.</summary>
public sealed record LlmGoogleCredentials
{
    public required string ServiceAccountJson { get; init; }
    public override string ToString() => "LlmGoogleCredentials { protected }";
}
