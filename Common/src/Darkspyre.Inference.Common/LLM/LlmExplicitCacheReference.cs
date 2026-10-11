using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>Provider cache identity. Possession does not establish scope or authorize a request.</summary>
/// <param name="ExpiresAt">When the provider discards the cached content, or null when unknown.</param>
/// <param name="ResourceName">The provider's name for the cached content.</param>
/// <param name="ModelId">The model the cache was created for.</param>
/// <param name="ScopeId">The scope the cache belongs to.</param>
public sealed record LlmExplicitCacheReference(
    string ResourceName, string ModelId, string ScopeId, DateTimeOffset? ExpiresAt = null)
{
    /// <summary>The provider's name for the cached content. Must not be blank.</summary>
    [JsonRequired]
    public string ResourceName { get; init => field = Required(value); } = Required(ResourceName);

    /// <summary>The model the cache was created for. Must not be blank.</summary>
    [JsonRequired]
    public string ModelId { get; init => field = Required(value); } = Required(ModelId);

    /// <summary>Identifies the connection scope the cache was created under. Must not be blank.</summary>
    [JsonRequired]
    public string ScopeId { get; init => field = Required(value); } = Required(ScopeId);

    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}
