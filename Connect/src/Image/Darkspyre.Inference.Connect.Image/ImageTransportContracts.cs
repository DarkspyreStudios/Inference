namespace Darkspyre.Inference.Connect.Image;

public sealed record ImageTransportPreparationRequest
{
    public required ImageWorkflow Workflow { get; init; }

    public required string PositivePrompt { get; init; }

    public string NegativePrompt { get; init; } = string.Empty;

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required long Seed { get; init; }

    public required string CacheKey { get; init; }

    public string Label { get; init; } = string.Empty;

    public ImageReference? Reference { get; init; }
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
public sealed record ImageTransportOutput(string FileName, string ProviderState);

public sealed record ImageTransportJobSnapshot(
    ImageTransportJobState State,
    ImageTransportOutput? Output = null,
    string Error = "",
    string Diagnostics = "{}");

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

    PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request);

    Task<ImageProviderHealth> TestAsync(
        ImageConnection connection,
        ImageWorkflow? workflow = null,
        CancellationToken cancellationToken = default);

    Task<IImageTransportProgressConnection?> TryConnectProgressAsync(
        ImageConnection connection,
        CancellationToken cancellationToken = default);

    Task UploadReferenceAsync(
        ImageConnection connection,
        ImageReference reference,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<string> SubmitAsync(
        ImageConnection connection,
        PreparedImageTransportRequest request,
        CancellationToken cancellationToken = default);

    Task<ImageTransportJobSnapshot> GetJobAsync(
        ImageConnection connection,
        string providerJobId,
        ImageWorkflow workflow,
        CancellationToken cancellationToken = default);

    Task<byte[]> DownloadAsync(
        ImageConnection connection,
        ImageTransportOutput output,
        CancellationToken cancellationToken = default);

    Task<bool> TryCancelAsync(
        ImageConnection connection,
        string providerJobId,
        bool interruptRunning,
        CancellationToken cancellationToken = default);
}
