using System.Collections.ObjectModel;
using System.Diagnostics;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.Image.Internal;

namespace Darkspyre.Inference.Connect.Image;

internal sealed class ConnectImageInference : IImageInference, IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly TimeSpan _pollInterval;
    private IImageTransport? _transport;
    private ImageConnection? _connection;
    private ImageWorkflow? _workflow;
    private XaiFamilyBinding? _xai;
    private IInferenceResourceAccess? _resources;

    internal ConnectImageInference(IImageTransport transport, ImageConnection connection,
        ImageWorkflow workflow, XaiFamilyBinding? xai, IInferenceResourceAccess? resources,
        TimeSpan pollInterval)
    {
        _transport = transport;
        _connection = connection;
        _workflow = workflow with { TransportConfiguration = workflow.TransportConfiguration.ValueKind == System.Text.Json.JsonValueKind.Undefined
            ? default : workflow.TransportConfiguration.Clone() };
        _xai = xai;
        _resources = resources;
        _pollInterval = pollInterval;
    }

    public InferenceImplementationDescriptor Descriptor { get; } = new(
        "darkspyre.connect.image", "Darkspyre Connect Image",
        InferenceImplementationKind.Connect, InferenceTask.ImageGeneration);

    public async Task<ImageCapabilities> GetCapabilitiesAsync(string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        ImageConnection connection = CaptureConnection(_connection!);
        ValidateBinding(connection, _xai);
        ImageCapabilities result = await _transport!.GetCapabilitiesAsync(connection, _workflow!, modelId, _xai, lease.Token).ConfigureAwait(false);
        Fence(lease.Token);
        return result;
    }

    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request,
        IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.References);
        IImageTransport transport = _transport!;
        ImageWorkflow workflow = _workflow!;
        ImageConnection connection = CaptureConnection(_connection!);
        XaiFamilyBinding? xai = _xai;
        IInferenceResourceAccess? resources = _resources;
        InferenceMediaSource[] references = request.References.ToArray();
        ValidateBinding(connection, xai);
        if (!string.Equals(request.ModelId, workflow.Id, StringComparison.Ordinal))
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The requested workflow is not bound.");
        if (request.Count < 1)
            throw new ArgumentOutOfRangeException(nameof(request), "Image count must be positive.");
        if (references.Any(source => source is not (BinaryMediaSource or UrlMediaSource or FileMediaSource or ResourceMediaSource)))
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The image source variant is unsupported.");
        if (connection.Transport == ImageTransport.ComfyUi
            && (request.Count != 1 || request.Steps is not null || request.GuidanceScale is not null
                || references.Length > 1 || references.Any(source => source is not (BinaryMediaSource or ResourceMediaSource))))
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "ComfyUI does not support the requested controls or source kinds.");
        if (references.Any(source => source is ResourceMediaSource) && resources is null)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "Logical image references require caller-owned resource access.");
        if (workflow.RequiresReferenceImage && references.Length == 0)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The workflow requires a reference image.");
        if (xai is not null)
        {
            xai = await xai.AcquireBoundAsync(snapshot =>
            {
                if (!string.Equals(connection.Endpoint.TrimEnd('/'), snapshot.Endpoint.TrimEnd('/'), StringComparison.Ordinal))
                    throw new ArgumentException("The image endpoint does not match the selected xAI region.");
            }, lease.Token).ConfigureAwait(false);
            Fence(lease.Token);
            connection = connection with { Authorization = xai.Authorization };
        }
        ImageCapabilities capabilities = await transport.GetCapabilitiesAsync(connection, workflow, request.ModelId, xai, lease.Token).ConfigureAwait(false);
        Fence(lease.Token);
        ValidateCapabilities(request, references, capabilities);
        var receipts = new List<ImagePreparedReference>();
        foreach (InferenceMediaSource source in references)
        {
            Fence(lease.Token);
            receipts.Add(await transport.ResolveReferenceAsync(connection, source, xai, resources, lease.Token).ConfigureAwait(false));
            Fence(lease.Token);
        }
        long? seed = request.Seed ?? (connection.Transport == ImageTransport.ComfyUi ? Random.Shared.NextInt64() : null);
        PreparedImageTransportRequest prepared = transport.Prepare(new ImageTransportPreparationRequest
        {
            Workflow = workflow, PositivePrompt = request.Prompt, NegativePrompt = request.NegativePrompt,
            Width = request.Width, Height = request.Height, Seed = seed,
            Steps = request.Steps, GuidanceScale = request.GuidanceScale, Count = request.Count,
            CacheKey = Guid.NewGuid().ToString("N"), Label = workflow.Label,
            References = receipts.AsReadOnly()
        });
        Fence(lease.Token);
        progress?.Report(new(ImageGenerationStage.Queued));
        string? jobId = null;
        long started = Stopwatch.GetTimestamp();
        try
        {
            jobId = await transport.SubmitAsync(connection, prepared, xai, lease.Token).ConfigureAwait(false);
            Fence(lease.Token);
            while (true)
            {
                ImageTransportJobSnapshot snapshot = await transport.GetJobAsync(connection, jobId, workflow, xai, lease.Token).ConfigureAwait(false);
                Fence(lease.Token);
                switch (snapshot.State)
                {
                    case ImageTransportJobState.Queued:
                        progress?.Report(new(ImageGenerationStage.Queued));
                        break;
                    case ImageTransportJobState.Generating:
                        progress?.Report(new(ImageGenerationStage.Generating));
                        break;
                    case ImageTransportJobState.Completed:
                        if (snapshot.Outputs is not { Count: > 0 })
                            throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The image provider completed without output artifacts.");
                        var images = new List<GeneratedImage>();
                        foreach (ImageTransportOutput output in snapshot.Outputs.ToArray())
                        {
                            ArgumentNullException.ThrowIfNull(output);
                            ImageTransportDownload download = await transport.DownloadAsync(connection, output, xai, lease.Token).ConfigureAwait(false);
                            Fence(lease.Token);
                            byte[] bytes = download.Data.ToArray();
                            string? contentType = download.ContentType ?? output.ContentType;
                            InferenceResourceDescriptor? resource = output.Resource;
                            if (resources is not null)
                                resource = await ImageResourceOperations.CommitAsync(resources, output.FileName, contentType, bytes, Fence, lease.Token).ConfigureAwait(false);
                            Fence(lease.Token);
                            images.Add(new GeneratedImage
                            { ContentType = contentType, Data = bytes, Seed = seed, File = output.File, Resource = resource });
                        }
                        Fence(lease.Token);
                        progress?.Report(new(ImageGenerationStage.Completed));
                        return new ImageGenerationResult
                        {
                            Images = images.AsReadOnly(),
                            Provenance = new InferenceProvenance
                            {
                                ImplementationId = Descriptor.Id, ModelId = workflow.Id, ModelRevision = workflow.Version,
                                Runtime = transport.Descriptor.DisplayName, Backend = "connect", Elapsed = Stopwatch.GetElapsedTime(started)
                            }
                        };
                    case ImageTransportJobState.Failed:
                        if (snapshot.Error?.Resource is { } resourceFailure)
                            throw new InferenceResourceOperationException(snapshot.Error.Code,
                                "The image provider reported a resource operation failure.", resourceFailure);
                        throw new InferenceException(snapshot.Error?.Code ?? InferenceFailureCode.ExecutionFailed,
                            snapshot.Error?.Message ?? "The image provider failed the generation job.");
                    default:
                        throw new InferenceException(InferenceFailureCode.ProtocolFailure, "The image provider returned an unknown job state.");
                }
                await Task.Delay(_pollInterval, lease.Token).ConfigureAwait(false);
                Fence(lease.Token);
            }
        }
        catch (OperationCanceledException error) when (jobId is not null)
        {
            try { await transport.TryCancelAsync(connection, jobId, interruptRunning: true, xai, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    private static void ValidateCapabilities(ImageGenerationRequest request, InferenceMediaSource[] references, ImageCapabilities capabilities)
    {
        if (request.Count > capabilities.MaximumImageCount
            || (request.NegativePrompt is not null && !capabilities.SupportsNegativePrompt)
            || (request.Seed is not null && !capabilities.SupportsSeed)
            || (request.Steps is not null && !capabilities.SupportsSteps)
            || (request.GuidanceScale is not null && !capabilities.SupportsGuidanceScale)
            || (capabilities.MaximumReferences is { } maximum && references.Length > maximum)
            || references.Any(source => !capabilities.SupportedReferenceKinds.Contains(source switch
            {
                BinaryMediaSource => InferenceMediaSourceKind.Binary,
                UrlMediaSource => InferenceMediaSourceKind.Url,
                FileMediaSource => InferenceMediaSourceKind.ProviderFile,
                ResourceMediaSource => InferenceMediaSourceKind.Resource,
                _ => throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The image source variant is unsupported.")
            })))
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The selected image transport does not support the requested controls or references.");
    }

    internal static void ValidateBinding(ImageConnection connection, XaiFamilyBinding? xai)
    {
        if (connection.Transport != ImageTransport.XaiImagine)
        {
            if (xai is not null || connection.Xai is not null)
                throw new ArgumentException("This image transport does not accept an xAI binding.");
            if (connection.Transport == ImageTransport.ComfyUi && connection.Authorization is not null)
                throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "ComfyUI dynamic authorization is not qualified; use static protected headers.");
        }
        else if (connection.Xai is null || xai is null || connection.Xai != xai.Selection
            || (connection.Authorization is not null && !ReferenceEquals(connection.Authorization, xai.Authorization)))
            throw new ArgumentException("The image connection requires its matching borrowed xAI binding.");
    }

    private static ImageConnection CaptureConnection(ImageConnection connection) => connection with
    { AdditionalHeaders = new ReadOnlyDictionary<string, string>(connection.AdditionalHeaders.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)) };

    private void Fence(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("The image operation owner is retiring.", error, token); }
    }

    public void Dispose() => RetireAsync().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(RetireAsync());
    private Task RetireAsync() => _lifetime.RetireAsync(() =>
    {
        _transport = null; _connection = null; _workflow = null; _xai = null; _resources = null;
        return Task.CompletedTask;
    });
}
