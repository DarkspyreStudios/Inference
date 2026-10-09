using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.Video;

/// <summary>Provider-owned options. Common metadata admits no concrete provider variants.</summary>
public abstract record VideoProviderOptions
{
    protected VideoProviderOptions() { }
    public override string ToString() => "VideoProviderOptions { <options> }";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(VideoGenerationRequest), "generation")]
[JsonDerivedType(typeof(VideoEditRequest), "edit")]
[JsonDerivedType(typeof(VideoExtensionRequest), "extension")]
public abstract record VideoRequest
{
    private protected VideoRequest() { }
    public required string ModelId { get; init => field = VideoData.Identity(value); }
    public VideoProviderOptions? ProviderOptions { get; init; }
    public override string ToString() => "VideoRequest { <request> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoGenerationRequest : VideoRequest
{
    public string? Prompt { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? AspectRatio { get; init; }
    public int? ResolutionHeight { get; init; }
    public InferenceMediaSource? FirstFrame { get; init; }
    public InferenceMediaSource? LastFrame { get; init; }
    public IReadOnlyList<VideoKeyframe>? Keyframes { get; init => field = value is null ? null : VideoData.Copy(value); }
    public IReadOnlyList<InferenceMediaSource>? ReferenceImages { get; init => field = value is null ? null : VideoData.Copy(value); }
    public IReadOnlyList<VideoReferenceAudio>? ReferenceAudio { get; init => field = value is null ? null : VideoData.Copy(value); }
    public bool? GenerateAudio { get; init; }
    public override string ToString() => "VideoGenerationRequest { <request> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoEditRequest : VideoRequest
{
    public required string Prompt { get; init => field = VideoData.Required(value); }
    public required InferenceMediaSource SourceVideo { get; init => field = VideoData.Required(value); }
    public override string ToString() => "VideoEditRequest { <request> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoExtensionRequest : VideoRequest
{
    public string? Prompt { get; init; }
    public required InferenceMediaSource SourceVideo { get; init => field = VideoData.Required(value); }
    public TimeSpan? ExtensionDuration { get; init; }
    public override string ToString() => "VideoExtensionRequest { <request> }";
}

public sealed record VideoKeyframe(InferenceMediaSource Image, [property: JsonRequired] TimeSpan Timestamp)
{
    [JsonRequired] public InferenceMediaSource Image { get; init => field = VideoData.Required(value); } = VideoData.Required(Image);
    public override string ToString() => "VideoKeyframe { <reference> }";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(VideoVoiceReference), "voice")]
[JsonDerivedType(typeof(VideoAudioReference), "audio")]
public abstract record VideoReferenceAudio
{
    private protected VideoReferenceAudio() { }
    public override string ToString() => "VideoReferenceAudio { <reference> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoVoiceReference(string VoiceId) : VideoReferenceAudio
{
    [JsonRequired] public string VoiceId { get; init => field = VideoData.Identity(value); } = VideoData.Identity(VoiceId);
    public override string ToString() => "VideoVoiceReference { <reference> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoAudioReference(InferenceMediaSource Source) : VideoReferenceAudio
{
    [JsonRequired] public InferenceMediaSource Source { get; init => field = VideoData.Required(value); } = VideoData.Required(Source);
    public override string ToString() => "VideoAudioReference { <reference> }";
}

public enum VideoJobState { Pending = 0, Completed = 1, Expired = 2, Failed = 3 }
public enum VideoOperation { Generation = 0, Edit = 1, Extension = 2 }

public sealed record VideoJob(string ProviderJobId, InferenceResourceScope Scope)
{
    [JsonRequired] public string ProviderJobId { get; init => field = VideoData.Identity(value); } = VideoData.Identity(ProviderJobId);
    [JsonRequired] public InferenceResourceScope Scope { get; init => field = VideoData.Required(value); } = VideoData.Required(Scope);
    public override string ToString() => "VideoJob { <receipt> }";
}

public sealed record VideoOutput(InferenceResourceScope Scope, JsonElement ProviderState,
    string? ContentType = null, TimeSpan? Duration = null,
    DateTimeOffset? ExpiresAt = null, string? FileName = null,
    ProviderFileReference? File = null, InferenceResourceDescriptor? Resource = null)
{
    [JsonRequired] public InferenceResourceScope Scope { get; init => field = VideoData.Required(value); } = VideoData.Required(Scope);
    [JsonRequired] public JsonElement ProviderState { get; init => field = VideoData.State(value); } = VideoData.State(ProviderState);
    public override string ToString() => "VideoOutput { <receipt> }";
}

public sealed record VideoJobSnapshot(VideoJob Job, VideoJobState State,
    double? ProgressPercent = null, VideoOutput? Output = null, InferenceResourceFailure? Error = null)
{
    [JsonRequired] public VideoJob Job { get; init => field = VideoData.Required(value); } = VideoData.Required(Job);
    [JsonRequired] public VideoJobState State { get; init => field = VideoData.Defined(value); } = VideoData.Defined(State);
    public double? ProgressPercent { get; init => field = VideoData.Finite(value); } = VideoData.Finite(ProgressPercent);
    /// <summary>The actual model reported by the provider; null when unknown.</summary>
    public string? ModelId { get; init; }
    public override string ToString() => "VideoJobSnapshot { <observation> }";
}

public sealed record VideoCapabilities
{
    public IReadOnlySet<VideoOperation> Operations { get; init => field = VideoData.Set(value); } = FrozenSet<VideoOperation>.Empty;
    public IReadOnlySet<InferenceMediaSourceKind> SourceKinds { get; init => field = VideoData.Set(value); } = FrozenSet<InferenceMediaSourceKind>.Empty;
    public TimeSpan? MinimumGenerationDuration { get; init; }
    public TimeSpan? MaximumGenerationDuration { get; init; }
    public TimeSpan? MinimumExtensionDuration { get; init; }
    public TimeSpan? MaximumExtensionDuration { get; init; }
    public IReadOnlyList<int>? ResolutionHeights { get; init => field = value is null ? null : VideoData.Copy(value); }
    public int? MaximumKeyframes { get; init; }
    public int? MaximumReferenceImages { get; init; }
    public int? MaximumReferenceAudio { get; init; }
    public InferenceSupport SupportsProgress { get; init; }
    public InferenceSupport SupportsLastFrame { get; init; }
    public InferenceSupport SupportsAudioControl { get; init; }
    public InferenceSupport SupportsPresetVoiceReferences { get; init; }
    public InferenceSupport SupportsSuppliedAudioReferences { get; init; }
}

/// <summary>Explicit native video operations and caller-resource materialization.</summary>
public interface IVideoInference : IAsyncDisposable
{
    Task<VideoCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default);
    Task<VideoJob> SubmitAsync(VideoRequest request, CancellationToken cancellationToken = default);
    Task<VideoJobSnapshot> GetAsync(VideoJob job, CancellationToken cancellationToken = default);
    /// <summary>Returns a caller-owned stream whose lifetime retains the download operation.</summary>
    Task<Stream> DownloadAsync(VideoOutput output, CancellationToken cancellationToken = default);
    Task<InferenceResourceDescriptor> MaterializeAsync(VideoOutput output, CancellationToken cancellationToken = default);
}

internal static class VideoData
{
    internal static T Required<T>(T? value) where T : class { ArgumentNullException.ThrowIfNull(value); return value; }
    internal static string Identity(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value; }
    internal static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    internal static double? Finite(double? value) => value is null || double.IsFinite(value.Value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    internal static JsonElement State(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) throw new ArgumentException("Video output requires actual provider state.", nameof(value));
        return value.Clone();
    }
    internal static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        T[] copy = value.ToArray();
        if (copy.Any(item => item is null)) throw new ArgumentException("Video collections cannot contain null entries.", nameof(value));
        return Array.AsReadOnly(copy);
    }
    internal static IReadOnlySet<T> Set<T>(IReadOnlySet<T> value) where T : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Select(Defined).ToFrozenSet();
    }
}
