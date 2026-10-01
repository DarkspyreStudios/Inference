using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Explicit cache creation inputs. A provider operation validates and freezes the prefix before dispatch.</summary>
public sealed record LlmContextCacheCreate
{
    public required string ModelId { get; init => field = CacheContractData.Required(value); }
    public string? Instructions { get; init; }
    public IReadOnlyList<LlmChatMessage> Messages { get; init => field = CacheContractData.Copy(value); } = [];
    public IReadOnlyList<LlmClientToolDefinition> Tools { get; init => field = CacheContractData.Copy(value); } = [];
    public LlmToolChoice? ToolChoice { get; init; }
    public string? DisplayName { get; init; }
    public required LlmContextCacheCreateCorrelation Correlation
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    }
    public IInferenceResourceAccess? Resources { get; init; }
    public TimeSpan? TimeToLive { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    public override string ToString() => "LlmContextCacheCreate { <request> }";
}

public sealed record LlmContextCacheCreateCorrelation(string OperationId, string ReconciliationDisplayName)
{
    [JsonRequired]
    public string OperationId { get; init => field = CacheContractData.Required(value); } = CacheContractData.Required(OperationId);
    [JsonRequired]
    public string ReconciliationDisplayName { get; init => field = CacheContractData.Required(value); } = CacheContractData.Required(ReconciliationDisplayName);
}

/// <summary>Observed cache facts. Expiry is required provider evidence, not a calculated request lifetime.</summary>
public sealed record LlmContextCache(
    LlmExplicitCacheReference Reference, string? DisplayName,
    DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt,
    [property: JsonRequired] DateTimeOffset ExpiresAt, LlmContextCacheUsage? Usage)
{
    [JsonRequired]
    public LlmExplicitCacheReference Reference
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    } = Reference ?? throw new ArgumentNullException(nameof(Reference));
}

public sealed record LlmContextCacheUsage(
    long? TotalTokens, long? TextCharacters, long? ImageCount,
    TimeSpan? AudioDuration, TimeSpan? VideoDuration);

public sealed record LlmContextCacheList(int PageSize, string? PageToken = null)
{
    [JsonRequired]
    public int PageSize { get; init => field = Positive(value); } = Positive(PageSize);

    private static int Positive(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return value;
    }
}

public sealed record LlmContextCachePage(IReadOnlyList<LlmContextCache> Items, string? NextPageToken)
{
    [JsonRequired]
    public IReadOnlyList<LlmContextCache> Items
    {
        get;
        init => field = CacheContractData.Copy(value);
    } = CacheContractData.Copy(Items);
}

public sealed record LlmContextCacheDeleteResult([property: JsonRequired] bool AlreadyAbsent);

/// <summary>Explicit provider cache operations. This contract alone does not enable a provider route.</summary>
public interface ILlmContextCacheClient
{
    Task<LlmContextCache> CreateAsync(LlmContextCacheCreate request, CancellationToken cancellationToken = default);
    Task<LlmContextCache> GetAsync(LlmExplicitCacheReference reference, CancellationToken cancellationToken = default);
    Task<LlmContextCachePage> ListAsync(LlmContextCacheList request, CancellationToken cancellationToken = default);
    Task<LlmContextCache> UpdateExpiryAsync(LlmExplicitCacheReference reference, DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);
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
