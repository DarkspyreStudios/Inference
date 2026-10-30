using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Explicit cache creation inputs. A provider operation validates and freezes the prefix before dispatch.</summary>
public sealed record LlmContextCacheCreate
{
    /// <summary>The model the cached prefix is built for.</summary>
    public required string ModelId { get; init => field = CacheContractData.Required(value); }
    /// <summary>System instructions to cache.</summary>
    public string? Instructions { get; init; }
    /// <summary>Conversation messages to cache.</summary>
    public IReadOnlyList<LlmChatMessage> Messages { get; init => field = CacheContractData.Copy(value); } = [];
    /// <summary>Tool definitions to cache.</summary>
    public IReadOnlyList<LlmClientToolDefinition> Tools { get; init => field = CacheContractData.Copy(value); } = [];
    /// <summary>The tool choice to cache with the tools.</summary>
    public LlmToolChoice? ToolChoice { get; init; }
    /// <summary>A display name for the cache. A provider that names caches by the correlation requires it to match <see cref="LlmContextCacheCreateCorrelation.ReconciliationDisplayName"/>.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Identities that let the caller find the cache again after an uncertain create.</summary>
    public required LlmContextCacheCreateCorrelation Correlation
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    }
    /// <summary>Access to inference resources the cached messages reference.</summary>
    public IInferenceResourceAccess? Resources { get; init; }
    /// <summary>How long the cache lives after creation.</summary>
    public TimeSpan? TimeToLive { get; init; }
    /// <summary>When the cache expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Returns a fixed text that leaves out the request content.</summary>
    public override string ToString() => "LlmContextCacheCreate { <request> }";
}

/// <summary>Identities attached to a cache create so the caller can reconcile an uncertain outcome.</summary>
/// <param name="OperationId">The caller's identity for the create operation.</param>
/// <param name="ReconciliationDisplayName">The display name the provider stores on the cache, used to find it again.</param>
public sealed record LlmContextCacheCreateCorrelation(string OperationId, string ReconciliationDisplayName)
{
    /// <summary>The caller's identity for the create operation.</summary>
    [JsonRequired]
    public string OperationId { get; init => field = CacheContractData.Required(value); } = CacheContractData.Required(OperationId);
    /// <summary>The display name the provider stores on the cache, used to find it again.</summary>
    [JsonRequired]
    public string ReconciliationDisplayName { get; init => field = CacheContractData.Required(value); } = CacheContractData.Required(ReconciliationDisplayName);
}

/// <summary>Observed cache facts. Expiry is required provider evidence, not a calculated request lifetime.</summary>
/// <param name="DisplayName">The display name the provider stores on the cache, or null when it has none.</param>
/// <param name="CreatedAt">When the provider created the cache, or null when not reported.</param>
/// <param name="UpdatedAt">When the provider last updated the cache, or null when not reported.</param>
/// <param name="ExpiresAt">When the provider discards the cache.</param>
/// <param name="Usage">What the cache holds, or null when not reported.</param>
/// <param name="Reference">The provider cache identity.</param>
public sealed record LlmContextCache(
    LlmExplicitCacheReference Reference, string? DisplayName,
    DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt,
    [property: JsonRequired] DateTimeOffset ExpiresAt, LlmContextCacheUsage? Usage)
{
    /// <summary>The provider's identity for the cache.</summary>
    [JsonRequired]
    public LlmExplicitCacheReference Reference
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    } = Reference ?? throw new ArgumentNullException(nameof(Reference));
}

/// <summary>What a cache holds, as the provider reports it. Null fields are unreported.</summary>
/// <param name="TotalTokens">Tokens in the cached content.</param>
/// <param name="TextCharacters">Characters of cached text.</param>
/// <param name="ImageCount">Cached images.</param>
/// <param name="AudioDuration">Length of cached audio.</param>
/// <param name="VideoDuration">Length of cached video.</param>
public sealed record LlmContextCacheUsage(
    long? TotalTokens, long? TextCharacters, long? ImageCount,
    TimeSpan? AudioDuration, TimeSpan? VideoDuration);

/// <summary>A request for one page of caches.</summary>
/// <param name="PageSize">The most caches to return; must be positive.</param>
/// <param name="PageToken">The token from the previous page, or null for the first page.</param>
public sealed record LlmContextCacheList(int PageSize, string? PageToken = null)
{
    /// <summary>The most caches to return; always positive.</summary>
    [JsonRequired]
    public int PageSize { get; init => field = Positive(value); } = Positive(PageSize);

    private static int Positive(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }
}

/// <summary>One page of caches.</summary>
/// <param name="Items">The caches on this page.</param>
/// <param name="NextPageToken">The token for the next page, or null on the last page.</param>
public sealed record LlmContextCachePage(IReadOnlyList<LlmContextCache> Items, string? NextPageToken)
{
    /// <summary>The caches on this page.</summary>
    [JsonRequired]
    public IReadOnlyList<LlmContextCache> Items
    {
        get;
        init => field = CacheContractData.Copy(value);
    } = CacheContractData.Copy(Items);
}

/// <summary>The result of a cache delete.</summary>
/// <param name="AlreadyAbsent">True when the cache did not exist at delete time.</param>
public sealed record LlmContextCacheDeleteResult([property: JsonRequired] bool AlreadyAbsent);

/// <summary>Explicit provider cache operations. This contract alone does not enable a provider route. No phase-1 provider implements this; it is a forward seam kept under an L2 exception, and refuses with its documented message until a provider that implements it returns to the build.</summary>
public interface ILlmContextCacheClient
{
    /// <summary>Creates an explicit cache and returns its observed facts.</summary>
    Task<LlmContextCache> CreateAsync(LlmContextCacheCreate request, CancellationToken cancellationToken = default);
    /// <summary>Returns the current facts for a cache.</summary>
    Task<LlmContextCache> GetAsync(LlmExplicitCacheReference reference, CancellationToken cancellationToken = default);
    /// <summary>Returns one page of the caches the connection holds.</summary>
    Task<LlmContextCachePage> ListAsync(LlmContextCacheList request, CancellationToken cancellationToken = default);
    /// <summary>Sets a new expiry on a cache and returns its updated facts.</summary>
    Task<LlmContextCache> UpdateExpiryAsync(LlmExplicitCacheReference reference, DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);
    /// <summary>Deletes a cache.</summary>
    Task<LlmContextCacheDeleteResult> DeleteAsync(LlmExplicitCacheReference reference, CancellationToken cancellationToken = default);
}

internal static class CacheContractData
{
    internal static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }

    internal static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        T[] copy = value.ToArray();
        if (copy.Any(item => item is null))
            throw new ArgumentException("Cache collections cannot contain null entries.", nameof(value));
        return Array.AsReadOnly(copy);
    }
}
