using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.Image.Tests;

/// <summary>A controlled Image transport that can pause before capabilities and delegate reference resolution.</summary>
internal sealed class ImageTestTransport : IImageTransport
{
    public Func<Task>? CapabilityPause { get; init; }
    public IImageTransport? ReferenceMaterializer { get; init; }

    public static ImageWorkflow Workflow() =>
        new() { Id = "workflow", Label = "Workflow", Transport = ImageTransport.ComfyUi, TransportConfiguration = JsonElement.Parse("{}") };

    public static ImageGenerationRequest Request() => new() { ModelId = "workflow", Prompt = "draw" };

    public ImageTransportDescriptor Descriptor => new(ImageTransport.ComfyUi, "Controlled Image transport", "Managed fixture; no target qualification", new(InferenceSupport.Unsupported, InferenceSupport.Supported, InferenceSupport.Supported));

    public Task<IReadOnlyList<ImageWorkflow>> GetWorkflowsAsync(ImageConnection connection, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async Task<ImageCapabilities> GetCapabilitiesAsync(ImageConnection connection, ImageWorkflow workflow, string? modelId = null, CancellationToken cancellationToken = default)
    {
        if (CapabilityPause is not null) await CapabilityPause();
        return new()
        {
            SupportedSizes = [new(1024, 1024)],
            MaximumImageCount = 1,
            MaximumReferences = 1,
            SupportedReferenceKinds = new[] { InferenceMediaSourceKind.Binary }.ToFrozenSet()
        };
    }

    public PreparedImageTransportRequest Prepare(ImageTransportPreparationRequest request) => new("{}");

    public Task<ImageProviderHealth> TestAsync(ImageConnection connection, ImageWorkflow? workflow = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IImageTransportProgressConnection?> TryConnectProgressAsync(ImageConnection connection, CancellationToken cancellationToken = default) =>
        Task.FromResult<IImageTransportProgressConnection?>(null);

    public Task<ImagePreparedReference> UploadReferenceAsync(ImageConnection connection, ImageReference reference, Stream content, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ImagePreparedReference> ResolveReferenceAsync(ImageConnection connection, InferenceMediaSource source, IInferenceResourceAccess? resources = null, CancellationToken cancellationToken = default) =>
        ReferenceMaterializer?.ResolveReferenceAsync(connection, source, resources, cancellationToken)
            ?? Task.FromResult(new ImagePreparedReference(new() { Id = Guid.NewGuid() }, new("owner", "identity"), JsonElement.Parse("{}")));

    public Task<string> SubmitAsync(ImageConnection connection, PreparedImageTransportRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult("job");

    public Task<ImageTransportJobSnapshot> GetJobAsync(ImageConnection connection, string providerJobId, ImageWorkflow workflow, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ImageTransportJobSnapshot(ImageTransportJobState.Completed,
            [new ImageTransportOutput("first.png", new("owner", "identity"), JsonElement.Parse("{}"))]));

    public Task<ImageTransportDownload> DownloadAsync(ImageConnection connection, ImageTransportOutput output, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ImageTransportDownload("image/webp", new byte[] { 7, 8 }));

    public Task<bool> TryCancelAsync(ImageConnection connection, string providerJobId, bool interruptRunning, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
