using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Image;

public sealed record ImageTransportPreparationRequest
{
    public required ImageWorkflow Workflow { get; init; }

    public required string PositivePrompt { get; init; }

    public string? NegativePrompt { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public long? Seed { get; init; }

    public int? Steps { get; init; }

    public double? GuidanceScale { get; init; }

    public int Count { get; init; } = 1;

    public required string CacheKey { get; init; }

    public string Label { get; init; } = string.Empty;

    public IReadOnlyList<ImagePreparedReference> References
    {
        get;
        init => field = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    } = [];
}

public sealed record ImagePreparedReference(ImageReference Metadata,
    InferenceResourceScope Scope, JsonElement ProviderState,
    InferenceResourceReference? Resource = null)
{
    [JsonRequired]
    public ImageReference Metadata { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); }
        = Metadata ?? throw new ArgumentNullException(nameof(Metadata));
    [JsonRequired]
    public InferenceResourceScope Scope { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); }
        = Scope ?? throw new ArgumentNullException(nameof(Scope));
    [JsonRequired]
    public JsonElement ProviderState { get; init => field = value.Clone(); } = ProviderState.Clone();
}

/// <summary>Opaque adapter request state that can be persisted and submitted later.</summary>
public sealed record PreparedImageTransportRequest(string ProviderRequest);

public enum ImageTransportJobState
{
    Queued,
    Generating,
    Completed,
    Failed
}

/// <summary>Completed output metadata plus opaque adapter state needed to download it.</summary>
public sealed record ImageTransportOutput(string? FileName,
    InferenceResourceScope Scope, JsonElement ProviderState,
    string? ContentType = null, ProviderFileReference? File = null,
    InferenceResourceDescriptor? Resource = null)
{
    [JsonRequired]
    public InferenceResourceScope Scope { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); }
        = Scope ?? throw new ArgumentNullException(nameof(Scope));
    [JsonRequired]
    public JsonElement ProviderState { get; init => field = value.Clone(); } = ProviderState.Clone();
}

public sealed record ImageTransportDownload(string? ContentType, ReadOnlyMemory<byte> Data)
{
    [JsonRequired]
    public ReadOnlyMemory<byte> Data { get; init => field = value.ToArray(); } = Data.ToArray();
}

public sealed record ImageTransportJobSnapshot(
    [property: JsonRequired] ImageTransportJobState State,
    IReadOnlyList<ImageTransportOutput>? Outputs = null,
    InferenceResourceFailure? Error = null,
    JsonElement? Diagnostics = null)
{
    /// <summary>The actual model identity reported by the adapter; null when unknown.</summary>
    public string? ModelId { get; init; }

    /// <summary>A separately observed model revision; null when unknown.</summary>
    public string? ModelRevision { get; init; }

    public IReadOnlyList<ImageTransportOutput>? Outputs { get; init => field = value is null ? null : Array.AsReadOnly(value.ToArray()); }
        = Outputs is null ? null : Array.AsReadOnly(Outputs.ToArray());
    public JsonElement? Diagnostics { get; init => field = value?.Clone(); } = Diagnostics?.Clone();
}

public enum ImageTransportProgressKind
{
    Executing,
    Progress,
    Finished,
    Failed
}

public readonly record struct ImageTransportProgress(
    ImageTransportProgressKind Kind,
    string Error = "",
    int? Value = null,
    int? Maximum = null);

public sealed record ImageProviderHealth(
    bool IsHealthy,
    string Message,
    string Diagnostics = "{}");

public interface IImageTransportProgressConnection : IAsyncDisposable
{
    Task<ImageTransportProgress?> ReceiveAsync(
        string providerJobId,
        CancellationToken cancellationToken);
}

/// <summary>Provider-neutral image generation seam implemented by transport adapters.</summary>
public interface IImageTransport
{
    ImageTransportDescriptor Descriptor { get; }

    Task<IReadOnlyList<ImageWorkflow>> GetWorkflowsAsync(
        ImageConnection connection,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default);

    Task<ImageCapabilities> GetCapabilitiesAsync(
        ImageConnection connection,
        ImageWorkflow workflow,
        string? modelId = null,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default);

    PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request);

    Task<ImageProviderHealth> TestAsync(
        ImageConnection connection,
        ImageWorkflow? workflow = null,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default);

    Task<IImageTransportProgressConnection?> TryConnectProgressAsync(
        ImageConnection connection,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default);

    Task<ImagePreparedReference> UploadReferenceAsync(
        ImageConnection connection,
        ImageReference reference,
        Stream content,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default);

    Task<ImagePreparedReference> ResolveReferenceAsync(
        ImageConnection connection,
        InferenceMediaSource source,
        XaiFamilyBinding? xai = null,
        IInferenceResourceAccess? resources = null,
        CancellationToken cancellationToken = default);

    Task<string> SubmitAsync(
        ImageConnection connection,
        PreparedImageTransportRequest request,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default);

    Task<ImageTransportJobSnapshot> GetJobAsync(
        ImageConnection connection,
        string providerJobId,
        ImageWorkflow workflow,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default);

    Task<ImageTransportDownload> DownloadAsync(
        ImageConnection connection,
        ImageTransportOutput output,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default);

    Task<bool> TryCancelAsync(
        ImageConnection connection,
        string providerJobId,
        bool interruptRunning,
        XaiFamilyBinding? xai = null,
        CancellationToken cancellationToken = default);
}
