using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM;

public enum LlmFileOrder { Ascending = 0, Descending = 1 }
public enum LlmFileSort { CreatedAt = 0, FileName = 1, Size = 2 }
public enum LlmFileContentFormat { Original = 0, Text = 1 }

public sealed record LlmProviderFile(string Id, string FileName,
    long Bytes, [property: JsonRequired] DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt)
{
    [JsonRequired] public string Id { get; init => field = FileContractData.Identity(value); } = FileContractData.Identity(Id);
    [JsonRequired] public string FileName { get; init => field = FileContractData.Required(value); } = FileContractData.Required(FileName);
    [JsonRequired] public long Bytes { get; init => field = FileContractData.Nonnegative(value); } = FileContractData.Nonnegative(Bytes);
    public string? PublicUrl { get; init; }
    public DateTimeOffset? PublicUrlExpiresAt { get; init; }
    public InferenceResourceReference? Resource { get; init; }
    public override string ToString() => "LlmProviderFile { <metadata> }";
}

public sealed record LlmFileUploadOptions(TimeSpan? ExpiresAfter = null)
{
    public TimeSpan? ExpiresAfter { get; init => field = FileContractData.Expiry(value); } = FileContractData.Expiry(ExpiresAfter);
    public override string ToString() => "LlmFileUploadOptions { <options> }";
}

public sealed record LlmFileListRequest
{
    public int Limit { get; init => field = FileContractData.Limit(value); } = 100;
    public string? PaginationToken { get; init; }
    public LlmFileOrder? Order { get; init; }
    public LlmFileSort? Sort { get; init; }
    public string? Filter { get; init; }
    public override string ToString() => "LlmFileListRequest { <request> }";
}

public sealed record LlmFilePage(IReadOnlyList<LlmProviderFile> Items,
    string? PaginationToken, [property: JsonRequired] bool IsKnownLastPage)
{
    [JsonRequired] public IReadOnlyList<LlmProviderFile> Items { get; init => field = FileContractData.Copy(value); } = FileContractData.Copy(Items);
    public override string ToString() => "LlmFilePage { <page> }";
}

public sealed record LlmFileDeletion(string Id, [property: JsonRequired] bool Deleted)
{
    [JsonRequired] public string Id { get; init => field = FileContractData.Identity(value); } = FileContractData.Identity(Id);
    public override string ToString() => "LlmFileDeletion { <result> }";
}

public sealed record LlmFileDownloadOptions(LlmFileContentFormat? Format = null)
{
    public override string ToString() => "LlmFileDownloadOptions { <options> }";
}

public sealed record ResourceDownloadReceipt(long BytesWritten, string? ResponseContentType, long? DeclaredContentLength)
{
    [JsonRequired] public long BytesWritten { get; init => field = FileContractData.Nonnegative(value); } = FileContractData.Nonnegative(BytesWritten);
    public long? DeclaredContentLength { get; init => field = value is { } length ? FileContractData.Nonnegative(length) : null; }
        = DeclaredContentLength is { } length ? FileContractData.Nonnegative(length) : null;
    public override string ToString() => "ResourceDownloadReceipt { <transfer> }";
}

public sealed record XaiPublicUrlOptions(TimeSpan? ExpiresAfter = null)
{
    public TimeSpan? ExpiresAfter { get; init => field = FileContractData.Expiry(value); } = FileContractData.Expiry(ExpiresAfter);
    public override string ToString() => "XaiPublicUrlOptions { <options> }";
}

public sealed record XaiPublicUrl(string PublicUrl, DateTimeOffset? ExpiresAt)
{
    [JsonRequired] public string PublicUrl { get; init => field = FileContractData.Required(value); } = FileContractData.Required(PublicUrl);
    public override string ToString() => "XaiPublicUrl { <url> }";
}

public sealed record XaiPublicUrlRevocation(string Id, [property: JsonRequired] bool Revoked, string? PublicUrl)
{
    [JsonRequired] public string Id { get; init => field = FileContractData.Identity(value); } = FileContractData.Identity(Id);
    public override string ToString() => "XaiPublicUrlRevocation { <result> }";
}

public sealed record LlmFileCapabilities
{
    public bool Upload { get; init; }
    public bool List { get; init; }
    public bool Get { get; init; }
    public bool Download { get; init; }
    public bool Delete { get; init; }
    public bool TextDownload { get; init; }
    public bool PublicUrls { get; init; }
}

/// <summary>Explicit native file operations. Source and destination streams remain borrowed.</summary>
public interface ILlmFileClient
{
    Task<LlmProviderFile> UploadAsync(string connectionId, Stream source, string fileName,
        LlmFileUploadOptions? options = null, CancellationToken cancellationToken = default);
    Task<LlmFilePage> ListAsync(string connectionId, LlmFileListRequest request, CancellationToken cancellationToken = default);
    Task<LlmProviderFile> GetAsync(string connectionId, string fileId, CancellationToken cancellationToken = default);
    Task<ResourceDownloadReceipt> DownloadAsync(string connectionId, string fileId, Stream destination,
        LlmFileDownloadOptions? options = null, CancellationToken cancellationToken = default);
    Task<LlmFileDeletion> DeleteAsync(string connectionId, string fileId, CancellationToken cancellationToken = default);
}

public interface IXaiFilePublicUrlClient
{
    Task<XaiPublicUrl> CreatePublicUrlAsync(string connectionId, string fileId,
        XaiPublicUrlOptions? options = null, CancellationToken cancellationToken = default);
    Task<XaiPublicUrlRevocation> RevokePublicUrlAsync(string connectionId, string fileId, CancellationToken cancellationToken = default);
}

internal static class FileContractData
{
    internal static T Required<T>(T? value) where T : class { ArgumentNullException.ThrowIfNull(value); return value; }
    internal static string Identity(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value; }
    internal static long Nonnegative(long value) { ArgumentOutOfRangeException.ThrowIfNegative(value); return value; }
    internal static int Limit(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
        return value;
    }
    internal static TimeSpan? Expiry(TimeSpan? value)
    {
        if (value is { } duration && (duration.Ticks % TimeSpan.TicksPerSecond != 0
            || duration < TimeSpan.FromHours(1) || duration > TimeSpan.FromDays(30)))
            throw new ArgumentOutOfRangeException(nameof(value), "File expiry requires whole seconds between one hour and thirty days.");
        return value;
    }
    internal static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        T[] copy = value.ToArray();
        if (copy.Any(item => item is null)) throw new ArgumentException("Resource collections cannot contain null entries.", nameof(value));
        return Array.AsReadOnly(copy);
    }
}
