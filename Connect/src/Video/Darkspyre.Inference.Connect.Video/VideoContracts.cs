using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Video;

public enum VideoTransport { XaiImagine = 0 }

/// <summary>Short-lived connection data. Authorization is borrowed and never persisted as data.</summary>
public sealed record VideoConnection
{
    public required VideoTransport Transport { get; init => field = VideoContractData.Defined(value); }
    public required string Endpoint { get; init => field = VideoContractData.Identity(value); }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Caller-selected byte limit for encoded supplied reference audio. Binary and workspace audio require a positive value; null supplies no default.</summary>
    public long? MaximumEncodedReferenceAudioBytes { get; init; }
    public IReadOnlyDictionary<string, string> AdditionalHeaders { get; init => field = VideoContractData.Headers(value); }
        = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    public IProviderAuthorization? Authorization { get; init; }
    public XaiConnectionSelection? Xai { get; init; }
    public override string ToString() => "VideoConnection { <connection> }";
}

public sealed record XaiVideoUploadDestination(Uri UploadUrl)
{
    [JsonRequired] public Uri UploadUrl { get; init => field = VideoContractData.Required(value); } = VideoContractData.Required(UploadUrl);
    public override string ToString() => "XaiVideoUploadDestination { <destination> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record XaiVideoOptions(XaiFileStorageOptions? Storage = null,
    XaiVideoUploadDestination? Output = null, string? User = null) : VideoProviderOptions
{
    public override string ToString() => "XaiVideoOptions { <options> }";
}

/// <summary>
/// Native provider operations. The adapter validates receipt authority before effects, owns wire
/// encoding and download-origin rules, and borrows the entered binding and resource access.
/// </summary>
public interface IVideoTransport
{
    VideoTransport Transport { get; }
    InferenceSetupDefinition SetupDefinition { get; }
    Task<VideoCapabilities> GetCapabilitiesAsync(VideoConnection connection, string? modelId = null,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default);
    Task<VideoJob> SubmitAsync(VideoConnection connection, VideoRequest request,
        XaiFamilyBinding? xai = null, IInferenceResourceAccess? resources = null,
        CancellationToken cancellationToken = default);
    /// <summary>Validates known receipt incompatibilities before acquiring or refreshing the borrowed binding.</summary>
    Task<VideoJobSnapshot> GetAsync(VideoConnection connection, VideoJob job,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default);
    /// <summary>Validates known receipt incompatibilities before refresh. Returns an owned stream; OAuth never follows unqualified destinations.</summary>
    Task<Stream> DownloadAsync(VideoConnection connection, VideoOutput output,
        XaiFamilyBinding? xai = null, CancellationToken cancellationToken = default);
}

internal static class VideoContractData
{
    internal static T Required<T>(T? value) where T : class { ArgumentNullException.ThrowIfNull(value); return value; }
    internal static string Identity(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value; }
    internal static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    internal static IReadOnlyDictionary<string, string> Headers(IReadOnlyDictionary<string, string> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ReadOnlyDictionary<string, string>(value.ToDictionary(pair => Identity(pair.Key), pair => Required(pair.Value), StringComparer.OrdinalIgnoreCase));
    }
}
