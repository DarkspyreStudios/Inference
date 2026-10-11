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
    /// <summary>Initializes provider options in a derived type.</summary>
    protected VideoProviderOptions() { }
    /// <summary>Returns a fixed placeholder so option values never reach logs.</summary>
    public override string ToString() => "VideoProviderOptions { <options> }";
}

/// <summary>Base type for a video request, serialized with a <c>type</c> discriminator of <c>generation</c>, <c>edit</c> or <c>extension</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(VideoGenerationRequest), "generation")]
[JsonDerivedType(typeof(VideoEditRequest), "edit")]
[JsonDerivedType(typeof(VideoExtensionRequest), "extension")]
public abstract record VideoRequest
{
    private protected VideoRequest() { }
    /// <summary>The model that serves the request. It must not be blank.</summary>
    public required string ModelId { get; init => field = VideoData.Identity(value); }
    /// <summary>Provider-specific options, or null for none.</summary>
    public VideoProviderOptions? ProviderOptions { get; init; }
    /// <summary>Returns a fixed placeholder so request content never reaches logs.</summary>
    public override string ToString() => "VideoRequest { <request> }";
}

/// <summary>A request to generate a new video from a prompt, frames or references.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoGenerationRequest : VideoRequest
{
    /// <summary>The text prompt, or null when the video is driven by frames or references alone.</summary>
    public string? Prompt { get; init; }
    /// <summary>The requested video length, or null for the provider default.</summary>
    public TimeSpan? Duration { get; init; }
    /// <summary>The requested aspect ratio, such as <c>16:9</c>, or null for the provider default.</summary>
    public string? AspectRatio { get; init; }
    /// <summary>The requested frame height in pixels, or null for the provider default.</summary>
    public int? ResolutionHeight { get; init; }
    /// <summary>The image the video starts from, or null.</summary>
    public InferenceMediaSource? FirstFrame { get; init; }
    /// <summary>The image the video ends on, or null.</summary>
    public InferenceMediaSource? LastFrame { get; init; }
    /// <summary>Images the video passes through at given times, or null. The list is copied and must not contain null entries.</summary>
    public IReadOnlyList<VideoKeyframe>? Keyframes { get; init => field = value is null ? null : VideoData.Copy(value); }
    /// <summary>Images that guide the video content, or null. The list is copied and must not contain null entries.</summary>
    public IReadOnlyList<InferenceMediaSource>? ReferenceImages { get; init => field = value is null ? null : VideoData.Copy(value); }
    /// <summary>Voices or audio that guide the generated sound, or null. The list is copied and must not contain null entries.</summary>
    public IReadOnlyList<VideoReferenceAudio>? ReferenceAudio { get; init => field = value is null ? null : VideoData.Copy(value); }
    /// <summary>Whether the video has generated audio, or null for the provider default.</summary>
    public bool? GenerateAudio { get; init; }
    /// <summary>Returns a fixed placeholder so request content never reaches logs.</summary>
    public override string ToString() => "VideoGenerationRequest { <request> }";
}

/// <summary>A request to edit an existing video according to a prompt.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoEditRequest : VideoRequest
{
    /// <summary>The edit instruction.</summary>
    public required string Prompt { get; init => field = VideoData.Required(value); }
    /// <summary>The video to edit.</summary>
    public required InferenceMediaSource SourceVideo { get; init => field = VideoData.Required(value); }
    /// <summary>Returns a fixed placeholder so request content never reaches logs.</summary>
    public override string ToString() => "VideoEditRequest { <request> }";
}

/// <summary>A request to extend an existing video.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoExtensionRequest : VideoRequest
{
    /// <summary>A prompt for the added footage, or null.</summary>
    public string? Prompt { get; init; }
    /// <summary>The video to extend.</summary>
    public required InferenceMediaSource SourceVideo { get; init => field = VideoData.Required(value); }
    /// <summary>The length to add, or null for the provider default.</summary>
    public TimeSpan? ExtensionDuration { get; init; }
    /// <summary>Returns a fixed placeholder so request content never reaches logs.</summary>
    public override string ToString() => "VideoExtensionRequest { <request> }";
}

/// <summary>An image the generated video shows at a given time.</summary>
/// <param name="Image">The keyframe image.</param>
/// <param name="Timestamp">The time in the video at which the image appears.</param>
public sealed record VideoKeyframe(InferenceMediaSource Image, [property: JsonRequired] TimeSpan Timestamp)
{
    /// <summary>The keyframe image.</summary>
    [JsonRequired] public InferenceMediaSource Image { get; init => field = VideoData.Required(value); } = VideoData.Required(Image);
    /// <summary>Returns a fixed placeholder so reference content never reaches logs.</summary>
    public override string ToString() => "VideoKeyframe { <reference> }";
}

/// <summary>Base type for audio that guides video generation, serialized with a <c>type</c> discriminator of <c>voice</c> or <c>audio</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(VideoVoiceReference), "voice")]
[JsonDerivedType(typeof(VideoAudioReference), "audio")]
public abstract record VideoReferenceAudio
{
    private protected VideoReferenceAudio() { }
    /// <summary>Returns a fixed placeholder so reference content never reaches logs.</summary>
    public override string ToString() => "VideoReferenceAudio { <reference> }";
}

/// <summary>A preset provider voice used as an audio reference.</summary>
/// <param name="VoiceId">The provider voice identifier.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoVoiceReference(string VoiceId) : VideoReferenceAudio
{
    /// <summary>The provider voice identifier. It must not be blank.</summary>
    [JsonRequired] public string VoiceId { get; init => field = VideoData.Identity(value); } = VideoData.Identity(VoiceId);
    /// <summary>Returns a fixed placeholder so reference content never reaches logs.</summary>
    public override string ToString() => "VideoVoiceReference { <reference> }";
}

/// <summary>Supplied audio used as an audio reference.</summary>
/// <param name="Source">The audio media.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VideoAudioReference(InferenceMediaSource Source) : VideoReferenceAudio
{
    /// <summary>The audio media.</summary>
    [JsonRequired] public InferenceMediaSource Source { get; init => field = VideoData.Required(value); } = VideoData.Required(Source);
    /// <summary>Returns a fixed placeholder so reference content never reaches logs.</summary>
    public override string ToString() => "VideoAudioReference { <reference> }";
}

/// <summary>The state of a video job.</summary>
public enum VideoJobState
{
    /// <summary>The job is queued or running.</summary>
    Pending = 0,
    /// <summary>The job finished and has an output.</summary>
    Completed = 1,
    /// <summary>The provider discarded the job or its output.</summary>
    Expired = 2,
    /// <summary>The job failed.</summary>
    Failed = 3
}

/// <summary>A kind of video operation.</summary>
public enum VideoOperation
{
    /// <summary>Generating a new video.</summary>
    Generation = 0,
    /// <summary>Editing an existing video.</summary>
    Edit = 1,
    /// <summary>Extending an existing video.</summary>
    Extension = 2
}

/// <summary>A receipt for a submitted video job, used to poll it.</summary>
/// <param name="ProviderJobId">The provider job identifier.</param>
/// <param name="Scope">The endpoint and account scope the job belongs to.</param>
public sealed record VideoJob(string ProviderJobId, InferenceResourceScope Scope)
{
    /// <summary>The provider job identifier. It must not be blank.</summary>
    [JsonRequired] public string ProviderJobId { get; init => field = VideoData.Identity(value); } = VideoData.Identity(ProviderJobId);
    /// <summary>The endpoint and account scope the job belongs to.</summary>
    [JsonRequired] public InferenceResourceScope Scope { get; init => field = VideoData.Required(value); } = VideoData.Required(Scope);
    /// <summary>Returns a fixed placeholder so receipt content never reaches logs.</summary>
    public override string ToString() => "VideoJob { <receipt> }";
}

/// <summary>A receipt for a finished video, used to download or materialize it.</summary>
/// <param name="Scope">The endpoint and account scope the output belongs to.</param>
/// <param name="ProviderState">Provider data needed to fetch the output. It must not be null or undefined.</param>
/// <param name="ContentType">The media type, or null when unknown.</param>
/// <param name="Duration">The video length, or null when unknown.</param>
/// <param name="ExpiresAt">When the provider discards the output, or null when unknown.</param>
/// <param name="FileName">The file name, or null when unknown.</param>
/// <param name="File">The provider file holding the output, or null.</param>
/// <param name="Resource">The caller resource the output was saved to, or null.</param>
public sealed record VideoOutput(InferenceResourceScope Scope, JsonElement ProviderState,
    string? ContentType = null, TimeSpan? Duration = null,
    DateTimeOffset? ExpiresAt = null, string? FileName = null,
    ProviderFileReference? File = null, InferenceResourceDescriptor? Resource = null)
{
    /// <summary>The endpoint and account scope the output belongs to.</summary>
    [JsonRequired] public InferenceResourceScope Scope { get; init => field = VideoData.Required(value); } = VideoData.Required(Scope);
    /// <summary>Provider data needed to fetch the output. The value is cloned and must not be null or undefined.</summary>
    [JsonRequired] public JsonElement ProviderState { get; init => field = VideoData.State(value); } = VideoData.State(ProviderState);
    /// <summary>Returns a fixed placeholder so receipt content never reaches logs.</summary>
    public override string ToString() => "VideoOutput { <receipt> }";
}

/// <summary>The observed state of a video job at one point in time.</summary>
/// <param name="Job">The job observed.</param>
/// <param name="State">The job state.</param>
/// <param name="ProgressPercent">The progress from 0 to 100, or null when the provider does not report it.</param>
/// <param name="Output">The finished output, or null until the job completes.</param>
/// <param name="Error">The failure, or null unless the job failed.</param>
public sealed record VideoJobSnapshot(VideoJob Job, VideoJobState State,
    double? ProgressPercent = null, VideoOutput? Output = null, InferenceResourceFailure? Error = null)
{
    /// <summary>The job observed.</summary>
    [JsonRequired] public VideoJob Job { get; init => field = VideoData.Required(value); } = VideoData.Required(Job);
    /// <summary>The job state. It must be a defined value.</summary>
    [JsonRequired] public VideoJobState State { get; init => field = VideoData.Defined(value); } = VideoData.Defined(State);
    /// <summary>The progress from 0 to 100, or null when the provider does not report it. It must be finite.</summary>
    public double? ProgressPercent { get; init => field = VideoData.Finite(value); } = VideoData.Finite(ProgressPercent);
    /// <summary>The actual model reported by the provider; null when unknown.</summary>
    public string? ModelId { get; init; }
    /// <summary>Returns a fixed placeholder so observation content never reaches logs.</summary>
    public override string ToString() => "VideoJobSnapshot { <observation> }";
}

/// <summary>The video operations and limits a model supports.</summary>
public sealed record VideoCapabilities
{
    /// <summary>The operations the model supports.</summary>
    public IReadOnlySet<VideoOperation> Operations { get; init => field = VideoData.Set(value); } = FrozenSet<VideoOperation>.Empty;
    /// <summary>The kinds of media source the model accepts.</summary>
    public IReadOnlySet<InferenceMediaSourceKind> SourceKinds { get; init => field = VideoData.Set(value); } = FrozenSet<InferenceMediaSourceKind>.Empty;
    /// <summary>The shortest video the model generates, or null when unknown.</summary>
    public TimeSpan? MinimumGenerationDuration { get; init; }
    /// <summary>The longest video the model generates, or null when unknown.</summary>
    public TimeSpan? MaximumGenerationDuration { get; init; }
    /// <summary>The shortest extension the model adds, or null when unknown.</summary>
    public TimeSpan? MinimumExtensionDuration { get; init; }
    /// <summary>The longest extension the model adds, or null when unknown.</summary>
    public TimeSpan? MaximumExtensionDuration { get; init; }
    /// <summary>The frame heights in pixels the model supports, or null when unknown.</summary>
    public IReadOnlyList<int>? ResolutionHeights { get; init => field = value is null ? null : VideoData.Copy(value); }
    /// <summary>The most keyframes a request can carry, or null when unknown.</summary>
    public int? MaximumKeyframes { get; init; }
    /// <summary>The most reference images a request can carry, or null when unknown.</summary>
    public int? MaximumReferenceImages { get; init; }
    /// <summary>The most audio references a request can carry, or null when unknown.</summary>
    public int? MaximumReferenceAudio { get; init; }
    /// <summary>Whether job snapshots report progress.</summary>
    public InferenceSupport SupportsProgress { get; init; }
    /// <summary>Whether a generation request can set the last frame.</summary>
    public InferenceSupport SupportsLastFrame { get; init; }
    /// <summary>Whether a generation request can turn generated audio on or off.</summary>
    public InferenceSupport SupportsAudioControl { get; init; }
    /// <summary>Whether a request can use preset provider voices as audio references.</summary>
    public InferenceSupport SupportsPresetVoiceReferences { get; init; }
    /// <summary>Whether a request can use supplied audio as audio references.</summary>
    public InferenceSupport SupportsSuppliedAudioReferences { get; init; }
}

/// <summary>Explicit native video operations and caller-resource materialization.</summary>
public interface IVideoInference : IAsyncDisposable
{
    /// <summary>Returns the capabilities of the given model, or of the default model when <paramref name="modelId"/> is null.</summary>
    Task<VideoCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default);
    /// <summary>Submits a video request and returns the job receipt.</summary>
    Task<VideoJob> SubmitAsync(VideoRequest request, CancellationToken cancellationToken = default);
    /// <summary>Returns the current state of a submitted job.</summary>
    Task<VideoJobSnapshot> GetAsync(VideoJob job, CancellationToken cancellationToken = default);
    /// <summary>Returns a caller-owned stream whose lifetime retains the download operation.</summary>
    Task<Stream> DownloadAsync(VideoOutput output, CancellationToken cancellationToken = default);
    /// <summary>Saves a finished output as a caller resource and returns its descriptor.</summary>
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
