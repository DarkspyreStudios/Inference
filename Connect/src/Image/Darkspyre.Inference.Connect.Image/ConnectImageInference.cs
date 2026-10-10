using System.Collections.ObjectModel;
using System.Diagnostics;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.Image.Internal;
using Darkspyre.Inference.Connect.Image.Providers;

namespace Darkspyre.Inference.Connect.Image;

internal sealed class ConnectImageInference : IImageInference, IDisposable, IAsyncDisposable
{
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly TimeSpan _pollInterval;
    private IImageTransport? _transport;
    private ImageConnection? _connection;
    private ImageWorkflow? _workflow;
    private IInferenceResourceAccess? _resources;
    private readonly List<(Exception Original, Exception Safe)> _cleanupFailures = [];

    internal ConnectImageInference(IImageTransport transport, ImageConnection connection,
        ImageWorkflow workflow, IInferenceResourceAccess? resources,
        TimeSpan pollInterval)
    {
        _transport = transport;
        _connection = connection;
        _workflow = workflow with
        {
            TransportConfiguration = workflow.TransportConfiguration.ValueKind == System.Text.Json.JsonValueKind.Undefined
            ? default : workflow.TransportConfiguration.Clone()
        };
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
        ValidateBinding(connection);
        ImageCapabilities result = await _transport!.GetCapabilitiesAsync(connection, _workflow!, modelId, lease.Token).ConfigureAwait(false);
        Fence(lease.Token);
        return result;
    }

    public async Task<ImageGenerationResult> GenerateAsync(ImageGenerationRequest request,
        IProgress<ImageGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using LlmOperationLifetime.Lease lease = _lifetime.Enter(cancellationToken);
        try { return await GenerateAdmittedAsync(request, progress, lease).ConfigureAwait(false); }
        catch (Exception error) { RecordCleanup(error); throw; }
    }

    private async Task<ImageGenerationResult> GenerateAdmittedAsync(ImageGenerationRequest request,
        IProgress<ImageGenerationProgress>? progress, LlmOperationLifetime.Lease lease)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.References);
        IImageTransport transport = _transport!;
        ImageWorkflow workflow = _workflow!;
        ImageConnection connection = CaptureConnection(_connection!);
        var generatedResources = new List<InferenceResourceCommitResult>();
        try
        {
            IInferenceResourceAccess? resources = _resources;
            InferenceMediaSource[] references = request.References.Select(source => source is BinaryMediaSource binary
                ? binary with { Data = binary.Data.ToArray() } : source).ToArray();
            ValidateBinding(connection);
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
            ImageCapabilities capabilities = await transport.GetCapabilitiesAsync(connection, workflow, request.ModelId, lease.Token).ConfigureAwait(false);
            Fence(lease.Token);
            ValidateCapabilities(request, references, capabilities);
            var receipts = new List<ImagePreparedReference>();
            foreach (InferenceMediaSource source in references)
            {
                Fence(lease.Token);
                ImagePreparedReference receipt = await transport.ResolveReferenceAsync(connection, source, resources, lease.Token).ConfigureAwait(false);
                receipts.Add(receipt);
                Fence(lease.Token);
            }
            long? seed = request.Seed ?? (connection.Transport == ImageTransport.ComfyUi ? Random.Shared.NextInt64() : null);
            PreparedImageTransportRequest prepared = transport.Prepare(new ImageTransportPreparationRequest
            {
                Workflow = workflow,
                PositivePrompt = request.Prompt,
                NegativePrompt = request.NegativePrompt,
                Width = request.Width,
                Height = request.Height,
                Seed = seed,
                Steps = request.Steps,
                GuidanceScale = request.GuidanceScale,
                Count = request.Count,
                CacheKey = Guid.NewGuid().ToString("N"),
                Label = workflow.Label,
                References = receipts.AsReadOnly()
            });
            Fence(lease.Token);
            progress?.Report(new(ImageGenerationStage.Queued));
            string? jobId = null;
            long started = Stopwatch.GetTimestamp();
            try
            {
                jobId = await transport.SubmitAsync(connection, prepared, lease.Token).ConfigureAwait(false);
                Fence(lease.Token);
                while (true)
                {
                    ImageTransportJobSnapshot snapshot = await transport.GetJobAsync(connection, jobId, workflow, lease.Token).ConfigureAwait(false);
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
                            generatedResources.EnsureCapacity(checked(generatedResources.Count + snapshot.Outputs.Count));
                            foreach (ImageTransportOutput output in snapshot.Outputs.ToArray())
                            {
                                ArgumentNullException.ThrowIfNull(output);
                                ImageTransportDownload download = await transport.DownloadAsync(connection, output, lease.Token).ConfigureAwait(false);
                                Fence(lease.Token);
                                byte[] bytes = download.Data.ToArray();
                                string? contentType = download.ContentType ?? output.ContentType;
                                InferenceResourceDescriptor? resource = output.Resource;
                                InferenceResourceSelection? selection = null;
                                if (resources is not null)
                                {
                                    InferenceResourceCommitResult committed = await ImageResourceOperations.CommitAsync(resources, output.FileName, contentType, bytes,
                                        Fence, generatedResources.Add, lease.Token).ConfigureAwait(false);
                                    resource = committed.Resource;
                                    selection = committed.Selection;
                                    contentType ??= committed.Resource.ContentType;
                                }
                                Fence(lease.Token);
                                images.Add(new GeneratedImage
                                {
                                    ContentType = contentType,
                                    Data = bytes,
                                    Seed = seed,
                                    File = output.File,
                                    Resource = resource,
                                    Selection = selection
                                });
                            }
                            Fence(lease.Token);
                            progress?.Report(new(ImageGenerationStage.Completed));
                            return new ImageGenerationResult
                            {
                                Images = images.AsReadOnly(),
                                Provenance = new InferenceProvenance
                                {
                                    ImplementationId = Descriptor.Id,
                                    ModelId = snapshot.ModelId,
                                    ModelRevision = snapshot.ModelRevision,
                                    Runtime = transport.Descriptor.DisplayName,
                                    Backend = "connect",
                                    Elapsed = Stopwatch.GetElapsedTime(started)
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
                try { await transport.TryCancelAsync(connection, jobId, interruptRunning: true, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception cleanup) when (cleanup is not OutOfMemoryException) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }
        catch (Exception error)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(LlmResourcePreparation.WithGeneratedResources(error, generatedResources));
            throw;
        }
    }

    private static void ValidateCapabilities(ImageGenerationRequest request, InferenceMediaSource[] references, ImageCapabilities capabilities)
    {
        if (request.Count > capabilities.MaximumImageCount
            || (!string.IsNullOrEmpty(request.NegativePrompt) && (capabilities.SupportsNegativePrompt == InferenceSupport.Unsupported))
            || (request.Seed is not null && (capabilities.SupportsSeed == InferenceSupport.Unsupported))
            || (request.Steps is not null && (capabilities.SupportsSteps == InferenceSupport.Unsupported))
            || (request.GuidanceScale is not null && (capabilities.SupportsGuidanceScale == InferenceSupport.Unsupported))
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

    internal static void ValidateBinding(ImageConnection connection)
    {
        if (connection.Transport == ImageTransport.ComfyUi && connection.Authorization is not null)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "ComfyUI dynamic authorization is not qualified; use static protected headers.");
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
        _transport = null; _connection = null; _workflow = null; _resources = null;
        if (_cleanupFailures.Count == 1) return Task.FromException(_cleanupFailures[0].Safe);
        if (_cleanupFailures.Count > 1) return Task.FromException(new AggregateException(_cleanupFailures.Select(item => item.Safe)));
        return Task.CompletedTask;
    });

    private void RecordCleanup(Exception error)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        lock (_cleanupFailures)
        {
            while (pending.TryPop(out Exception? current))
            {
                if (!visited.Add(current)) continue;
                if (current is InferenceResourceOperationException resource && resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup
                    || current is InferenceResourceOperationCanceledException canceled && canceled.Failure.Phase == InferenceResourceOperationPhase.Cleanup)
                {
                    Exception actual = ImageResourceOperations.ActualCleanup(current);
                    if (!_cleanupFailures.Any(existing => ReferenceEquals(existing.Original, actual))) _cleanupFailures.Add((actual, current));
                    continue;
                }
                if (current is AggregateException aggregate)
                    for (int index = aggregate.InnerExceptions.Count - 1; index >= 0; index--) pending.Push(aggregate.InnerExceptions[index]);
                else if (current is ImageResourceOperations.FailureEvidence evidence) pending.Push(evidence.Original);
                else if (current.InnerException is { } inner) pending.Push(inner);
            }
        }
    }
}
