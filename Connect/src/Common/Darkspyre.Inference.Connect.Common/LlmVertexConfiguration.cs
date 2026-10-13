using System.Net;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>How the Vertex AI client authenticates to Google Cloud.</summary>
public enum LlmGoogleAuthenticationMode
{
    /// <summary>Google application default credentials; no stored key.</summary>
    ApplicationDefault,
    /// <summary>A service-account key JSON stored in <see cref="LlmGoogleCredentials"/>.</summary>
    ServiceAccount,
    /// <summary>Global projectless Vertex express mode with a Google Cloud API key. Gemini only; no model listing.</summary>
    ApiKey
}

/// <summary>Non-secret Google Cloud connection selection. Credentials remain in LlmCredential.</summary>
public sealed record LlmVertexConfiguration
{
    /// <summary>The Google Cloud project that hosts the Vertex AI requests.</summary>
    public string? ProjectId { get; init; }
    /// <summary>The Vertex AI location, such as a region name or <c>global</c>.</summary>
    public string? Location { get; init; }
    /// <summary>How the client authenticates to Google Cloud.</summary>
    public LlmGoogleAuthenticationMode AuthenticationMode { get; init; }
    /// <summary>The Cloud Storage location for derived context-cache staging, or null when no staging is configured.</summary>
    public LlmVertexCacheStaging? CacheStaging { get; init; }
}

/// <summary>Caller-managed derived cache staging. The caller controls permissions and unchanged-object retention.</summary>
public sealed record LlmVertexCacheStaging(string Bucket, string ObjectPrefix)
{
    /// <summary>The Cloud Storage bucket name. Setting it throws when the value is not a valid bucket name.</summary>
    [JsonRequired]
    public string Bucket { get; init => field = ValidateBucket(value); } = ValidateBucket(Bucket);
    /// <summary>The object name prefix under the bucket. Setting it throws when the value contains control characters, <c>#</c> or <c>?</c>.</summary>
    [JsonRequired]
    public string ObjectPrefix { get; init => field = ValidatePrefix(value); } = ValidatePrefix(ObjectPrefix);
    /// <summary>Returns a diagnostic string without the bucket or prefix.</summary>
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
    /// <summary>The service-account key JSON.</summary>
    public required string ServiceAccountJson { get; init; }
    /// <summary>Returns a diagnostic string without the key.</summary>
    public override string ToString() => "LlmGoogleCredentials { protected }";
}
