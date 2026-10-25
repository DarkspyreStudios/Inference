using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Image;

/// <summary>The inputs a transport turns into a prepared provider request.</summary>
public sealed record ImageTransportPreparationRequest
{
    /// <summary>The workflow that generates the image.</summary>
    public required ImageWorkflow Workflow { get; init; }

    /// <summary>The prompt describing what the image shows.</summary>
    public required string PositivePrompt { get; init; }

    /// <summary>A prompt describing what the image must avoid, or null.</summary>
    public string? NegativePrompt { get; init; }

    /// <summary>The image width in pixels, or null for the workflow default.</summary>
    public int? Width { get; init; }

    /// <summary>The image height in pixels, or null for the workflow default.</summary>
    public int? Height { get; init; }

    /// <summary>The random seed, or null for a random one.</summary>
    public long? Seed { get; init; }

    /// <summary>The number of sampling steps, or null for the workflow default.</summary>
    public int? Steps { get; init; }

    /// <summary>The guidance scale, or null for the workflow default.</summary>
    public double? GuidanceScale { get; init; }

    /// <summary>The number of images to generate.</summary>
    public int Count { get; init; } = 1;

    /// <summary>A unique key for the request that names its output files.</summary>
    public required string CacheKey { get; init; }

    /// <summary>A display label for the request, such as the workflow label.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Reference images already prepared for the provider. The list is copied.</summary>
    public IReadOnlyList<ImagePreparedReference> References
    {
        get;
        init => field = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    } = [];
}

/// <summary>A reference image the provider can use, with the state needed to pass it to a request.</summary>
/// <param name="Metadata">The reference image metadata.</param>
/// <param name="Scope">The endpoint and account scope the reference belongs to.</param>
/// <param name="ProviderState">Provider data that identifies the uploaded or resolved image.</param>
/// <param name="Resource">The caller resource the image came from, or null.</param>
public sealed record ImagePreparedReference(ImageReference Metadata,
    InferenceResourceScope Scope, JsonElement ProviderState,
    InferenceResourceReference? Resource = null)
{
    /// <summary>The reference image metadata.</summary>
    [JsonRequired]
    public ImageReference Metadata { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); }
        = Metadata ?? throw new ArgumentNullException(nameof(Metadata));
    /// <summary>The endpoint and account scope the reference belongs to.</summary>
    [JsonRequired]
    public InferenceResourceScope Scope { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); }
        = Scope ?? throw new ArgumentNullException(nameof(Scope));
    /// <summary>Provider data that identifies the uploaded or resolved image. The value is cloned.</summary>
    [JsonRequired]
    public JsonElement ProviderState { get; init => field = value.Clone(); } = ProviderState.Clone();

    /// <summary>Returns a fixed placeholder so receipt content never reaches logs.</summary>
    public override string ToString() => "ImagePreparedReference { <receipt> }";
}

/// <summary>Opaque adapter request state that can be persisted and submitted later.</summary>
/// <param name="ProviderRequest">The adapter's serialized request, which only the same adapter reads.</param>
public sealed record PreparedImageTransportRequest(string ProviderRequest)
{
    /// <summary>Returns a fixed placeholder so request content never reaches logs.</summary>
    public override string ToString() => "PreparedImageTransportRequest { <request> }";
}

/// <summary>The state of an image generation job.</summary>
public enum ImageTransportJobState
{
    /// <summary>The job waits in the provider queue.</summary>
    Queued,
    /// <summary>The job is running.</summary>
    Generating,
    /// <summary>The job finished.</summary>
    Completed,
    /// <summary>The job failed.</summary>
    Failed
}

/// <summary>Completed output metadata plus opaque adapter state needed to download it.</summary>
/// <param name="FileName">The output's file name, or null when the provider gives none.</param>
/// <param name="ContentType">The output's media type, or null when unknown.</param>
/// <param name="File">The provider file that holds the output, or null when there is none.</param>
/// <param name="Resource">The workspace resource that holds the output, or null when there is none.</param>
/// <param name="Scope">The scope the output resource belongs to.</param>
/// <param name="ProviderState">Opaque adapter state needed to download the output.</param>
public sealed record ImageTransportOutput(string? FileName,
    InferenceResourceScope Scope, JsonElement ProviderState,
    string? ContentType = null, ProviderFileReference? File = null,
    InferenceResourceDescriptor? Resource = null)
{
    /// <summary>The endpoint and account scope the output belongs to.</summary>
    [JsonRequired]
    public InferenceResourceScope Scope { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); }
        = Scope ?? throw new ArgumentNullException(nameof(Scope));
    /// <summary>Provider data needed to download the output. The value is cloned.</summary>
    [JsonRequired]
    public JsonElement ProviderState { get; init => field = value.Clone(); } = ProviderState.Clone();

    /// <summary>Returns a fixed placeholder so output content never reaches logs.</summary>
    public override string ToString() => "ImageTransportOutput { <output> }";
}

/// <summary>The bytes of a downloaded image output.</summary>
/// <param name="ContentType">The media type, or null when unknown.</param>
/// <param name="Data">The image bytes.</param>
public sealed record ImageTransportDownload(string? ContentType, ReadOnlyMemory<byte> Data)
{
    /// <summary>The image bytes. The value is copied.</summary>
    [JsonRequired]
    public ReadOnlyMemory<byte> Data { get; init => field = value.ToArray(); } = Data.ToArray();
}

/// <summary>The observed state of an image generation job.</summary>
/// <param name="State">The job state.</param>
/// <param name="Outputs">The finished outputs, or null until the job completes.</param>
/// <param name="Error">The failure, or null unless the job failed.</param>
/// <param name="Diagnostics">Provider diagnostics, or null.</param>
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

    /// <summary>The finished outputs, or null until the job completes. The list is copied.</summary>
    public IReadOnlyList<ImageTransportOutput>? Outputs { get; init => field = value is null ? null : Array.AsReadOnly(value.ToArray()); }
        = Outputs is null ? null : Array.AsReadOnly(Outputs.ToArray());
    /// <summary>Provider diagnostics, or null. The value is cloned.</summary>
    public JsonElement? Diagnostics { get; init => field = value?.Clone(); } = Diagnostics?.Clone();

    /// <summary>Returns a fixed placeholder so snapshot content never reaches logs.</summary>
    public override string ToString() => "ImageTransportJobSnapshot { <snapshot> }";
}

/// <summary>The kind of a live progress signal.</summary>
public enum ImageTransportProgressKind
{
    /// <summary>The job started executing.</summary>
    Executing,
    /// <summary>The job reports a step count.</summary>
    Progress,
    /// <summary>The job finished.</summary>
    Finished,
    /// <summary>The job failed.</summary>
    Failed
}

/// <summary>A live progress signal for a running job.</summary>
/// <param name="Kind">The kind of signal.</param>
/// <param name="Error">The failure message for a failed signal, or empty.</param>
/// <param name="Value">The steps completed, or null.</param>
/// <param name="Maximum">The total steps, or null.</param>
public readonly record struct ImageTransportProgress(
    ImageTransportProgressKind Kind,
    string Error = "",
    int? Value = null,
    int? Maximum = null);

/// <summary>The result of testing an image provider connection.</summary>
/// <param name="IsHealthy">Whether the provider is reachable and usable.</param>
/// <param name="Message">A message for the user describing the result.</param>
/// <param name="Diagnostics">Diagnostic details as a JSON object.</param>
public sealed record ImageProviderHealth(
    bool IsHealthy,
    string Message,
    string Diagnostics = "{}");

/// <summary>An open connection that delivers live progress signals from the provider.</summary>
public interface IImageTransportProgressConnection : IAsyncDisposable
{
    /// <summary>Waits for the next progress signal for the given job. Returns null when the connection closes.</summary>
    Task<ImageTransportProgress?> ReceiveAsync(
        string providerJobId,
        CancellationToken cancellationToken);
}

/// <summary>Provider-neutral image generation seam implemented by transport adapters.</summary>
public interface IImageTransport
{
    /// <summary>Identifies the transport and the optional features it supports.</summary>
    ImageTransportDescriptor Descriptor { get; }

    /// <summary>Returns the workflows available on the connection.</summary>
    Task<IReadOnlyList<ImageWorkflow>> GetWorkflowsAsync(
        ImageConnection connection,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the capabilities of a workflow, for the given model when <paramref name="modelId"/> is set.</summary>
    Task<ImageCapabilities> GetCapabilitiesAsync(
        ImageConnection connection,
        ImageWorkflow workflow,
        string? modelId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Turns a preparation request into an opaque provider request that can be persisted and submitted later.</summary>
    PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request);

    /// <summary>Tests the connection, and the workflow when one is given.</summary>
    Task<ImageProviderHealth> TestAsync(
        ImageConnection connection,
        ImageWorkflow? workflow = null,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a live progress connection, or returns null when the provider offers none or cannot be reached.</summary>
    Task<IImageTransportProgressConnection?> TryConnectProgressAsync(
        ImageConnection connection,
        CancellationToken cancellationToken = default);

    /// <summary>Uploads a reference image to the provider and returns it prepared for a request.</summary>
    Task<ImagePreparedReference> UploadReferenceAsync(
        ImageConnection connection,
        ImageReference reference,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Names a reference image before it is uploaded, for a request that is prepared now and submitted later. The result
    /// goes into <see cref="ImageTransportPreparationRequest.References"/>; the caller uploads the same reference through
    /// <see cref="UploadReferenceAsync"/> before <see cref="SubmitAsync"/>. A request prepared with it stays valid across
    /// process restarts and is refused by a connection to another endpoint. A transport that cannot name an upload in
    /// advance throws an <see cref="InferenceException"/> with <see cref="InferenceFailureCode.UnsupportedFeature"/>.
    /// </summary>
    /// <gotcha>The reference is not uploaded. Submitting before the upload makes the provider fail the job for a missing input image.</gotcha>
    ImagePreparedReference PlanReference(ImageConnection connection, ImageReference reference) =>
        throw new InferenceException(InferenceFailureCode.UnsupportedFeature,
            $"{Descriptor.DisplayName} cannot name a reference image before it is uploaded.");

    /// <summary>Prepares a reference image from a media source, reading caller resources through <paramref name="resources"/> when given.</summary>
    Task<ImagePreparedReference> ResolveReferenceAsync(
        ImageConnection connection,
        InferenceMediaSource source,
        IInferenceResourceAccess? resources = null,
        CancellationToken cancellationToken = default);

    /// <summary>Submits a prepared request and returns the provider job identifier.</summary>
    Task<string> SubmitAsync(
        ImageConnection connection,
        PreparedImageTransportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the current state of a submitted job.</summary>
    Task<ImageTransportJobSnapshot> GetJobAsync(
        ImageConnection connection,
        string providerJobId,
        ImageWorkflow workflow,
        CancellationToken cancellationToken = default);

    /// <summary>Downloads the bytes of a finished output.</summary>
    Task<ImageTransportDownload> DownloadAsync(
        ImageConnection connection,
        ImageTransportOutput output,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a job from the provider queue and, when <paramref name="interruptRunning"/> is true, interrupts it if running. Returns false when cancellation failed.</summary>
    Task<bool> TryCancelAsync(
        ImageConnection connection,
        string providerJobId,
        bool interruptRunning,
        CancellationToken cancellationToken = default);
}
