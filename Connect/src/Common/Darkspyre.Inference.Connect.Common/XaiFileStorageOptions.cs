using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(XaiStoragePublicUrlFlag), "flag")]
[JsonDerivedType(typeof(XaiStoragePublicUrlConfiguration), "configuration")]
public abstract record XaiStoragePublicUrlRequest
{
    private protected XaiStoragePublicUrlRequest() { }
    public override string ToString() => "XaiStoragePublicUrlRequest { <options> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record XaiStoragePublicUrlFlag([property: JsonRequired] bool Enabled) : XaiStoragePublicUrlRequest
{
    public override string ToString() => "XaiStoragePublicUrlFlag { <options> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record XaiStoragePublicUrlConfiguration(XaiPublicUrlOptions Options) : XaiStoragePublicUrlRequest
{
    [JsonRequired] public XaiPublicUrlOptions Options { get; init => field = FileContractData.Required(value); } = FileContractData.Required(Options);
    public override string ToString() => "XaiStoragePublicUrlConfiguration { <options> }";
}

/// <summary>Explicit provider storage controls. Construction does not store or publish content.</summary>
public sealed record XaiFileStorageOptions(string FileName,
    TimeSpan? ExpiresAfter = null, XaiStoragePublicUrlRequest? PublicUrl = null)
{
    [JsonRequired] public string FileName { get; init => field = FileContractData.Required(value); } = FileContractData.Required(FileName);
    public TimeSpan? ExpiresAfter { get; init => field = StorageExpiry(value); } = StorageExpiry(ExpiresAfter);
    public override string ToString() => "XaiFileStorageOptions { <options> }";

    private static TimeSpan? StorageExpiry(TimeSpan? value)
    {
        if (value is { } duration && (duration.Ticks % TimeSpan.TicksPerSecond != 0 || duration > TimeSpan.FromDays(30)))
            throw new ArgumentOutOfRangeException(nameof(value), "Storage expiry requires whole seconds and cannot exceed thirty days.");
        return value;
    }
}
