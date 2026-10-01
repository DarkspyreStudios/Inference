using System.Diagnostics;
using System.Reflection;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Image;
using TensorSharp.Cuda;
using TensorSharp.Models.Direct;
using TensorSharp.Models.Media;

namespace Darkspyre.Inference.Tensor.Image.Supra;

public sealed class SupraImageInference : ITensorImageInference
{
    private const int DefaultSteps = 50;
    private const float DefaultGuidance = 3f;
    private const int MaximumCount = 4;

    private readonly ITensorModelPackManager _packs;
    private TensorModelPackDefinition? _definition;
    private readonly ISupraModelLoader _loader;
    private readonly TensorBackendKind _requestedBackend;
    private readonly TensorExecutionCoordinator _coordinator;
    private readonly bool _ownsCoordinator;
    private readonly TensorModelLifetime _lifetime;
    private TensorModelPackLease? _modelLease;
    private ISupraImageModel? _model;
    private TensorBackendKind? _loadedBackend;
    private bool _disposed;

    public SupraImageInference(
        ITensorModelPackManager packs,
        TensorBackendKind backend = TensorBackendKind.Auto,
        TensorExecutionCoordinator? coordinator = null)
        : this(packs, new SupraModelLoader(), backend, coordinator) { }

    internal SupraImageInference(ITensorModelPackManager packs, ISupraModelLoader loader,
        TensorBackendKind backend = TensorBackendKind.Auto, TensorExecutionCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(loader);
        _packs = packs;
        _loader = loader;
        _lifetime = new(this);

        _requestedBackend = backend;
        _coordinator = coordinator ?? new TensorExecutionCoordinator();
        _ownsCoordinator = coordinator is null;
    }

    public InferenceImplementationDescriptor Descriptor => SupraSetup.Definition.Implementation;

    public async Task<ImageCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await ValidateModelIdAsync(modelId, cancellationToken).ConfigureAwait(false);
        return new ImageCapabilities
        {
            SupportedSizes = [new(SupraDiT.ImageSize, SupraDiT.ImageSize)],
            SupportsNegativePrompt = false,
            SupportsSeed = true,
            SupportsSteps = true,
            SupportsGuidanceScale = true,
            MaximumImageCount = MaximumCount
        };
    }

    public async Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        IProgress<ImageGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        progress?.Report(new ImageGenerationProgress(ImageGenerationStage.Queued));
        await using TensorModelLifetime.ExecutionLease modelUse = await _lifetime.EnterExecutionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ValidateModelIdAsync(request.ModelId, cancellationToken).ConfigureAwait(false);
        TensorModelPackDefinition definition = _definition!;
        CancellationToken executionToken = modelUse.CancellationToken;
        ISupraImageModel model = _model
            ?? throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Supra image model was not loaded.");
        TensorBackendKind backend = _loadedBackend
            ?? throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The Supra backend was not loaded.");
        await using IAsyncDisposable execution = await _coordinator.AcquireAsync(
            definition.Key,
            backend,
            executionToken).ConfigureAwait(false);

        int steps = request.Steps ?? DefaultSteps;
        float guidance = checked((float)(request.GuidanceScale ?? DefaultGuidance));
        long baseSeed = request.Seed ?? 0;
        long started = Stopwatch.GetTimestamp();
        var images = new List<GeneratedImage>(request.Count);
        try
        {
            for (int imageIndex = 0; imageIndex < request.Count; imageIndex++)
            {
                executionToken.ThrowIfCancellationRequested();
                long seed = unchecked(baseSeed + imageIndex);
                progress?.Report(new ImageGenerationProgress(ImageGenerationStage.EncodingPrompt));
                int index = imageIndex;
                // Diffusion, decoding and PNG encoding are synchronous GPU and CPU compute.
                byte[] png = await Task.Run(() =>
                {
                    byte[] rgb = model.Generate(
                        request.Prompt,
                        seed,
                        steps,
                        guidance,
                        (value, maximum) => progress?.Report(new ImageGenerationProgress(
                            ImageGenerationStage.Generating,
                            index * maximum + value,
                            request.Count * maximum)),
                        () => progress?.Report(new ImageGenerationProgress(ImageGenerationStage.Decoding)),
                        executionToken);
                    return MediaCodecs.Image.EncodePng(
                        rgb,
                        SupraDiT.ImageSize,
                        SupraDiT.ImageSize,
                        channels: 3);
                }, executionToken).ConfigureAwait(false);
                images.Add(new GeneratedImage
                {
                    ContentType = "image/png",
                    Data = png,
                    Seed = seed
                });
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not InferenceException)
        {
            throw new InferenceException(
                InferenceFailureCode.ExecutionFailed,
                "Supra2-IMG failed while generating an image.",
                exception);
        }

        progress?.Report(new ImageGenerationProgress(ImageGenerationStage.Completed));
        return new ImageGenerationResult
        {
            Images = images,
            Provenance = new InferenceProvenance
            {
                ImplementationId = Descriptor.Id,
                ModelId = definition.Key.ModelId,
                ModelRevision = definition.Key.Revision,
                Runtime = "TensorSharp",
                RuntimeVersion = RuntimeVersion(),
                Backend = backend.ToString(),
                Elapsed = Stopwatch.GetElapsedTime(started)
            }
        };
    }

    public event EventHandler<TensorEngineStatus>? EngineStatusChanged
    {
        add => _lifetime.EngineStatusChanged += value;
        remove => _lifetime.EngineStatusChanged -= value;
    }

    public Task<TensorModelObservation> GetObservationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_lifetime.Observation);
    }

    public TensorBackendKind RequestedBackend => _requestedBackend;

    /// <summary>Start after Stop is an allowed no-op; it does not reset a stopped instance.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lifetime.StopAccepted)
        {
            return Task.CompletedTask;
        }

        return _lifetime.AcceptAsync(TensorEngineState.Starting, null, null,
            _ => Task.FromResult(new TensorEngineStatus(null, null, TensorEngineState.Started, DateTimeOffset.UtcNow)),
            cancellationToken);
    }

    public async Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        TensorModelPackDefinition definition;
        try
        {
            definition = await _packs.GetRequiredDefinitionAsync(model, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
        }

        try { SupraModelCompatibility.Validate(definition); }
        catch (ArgumentException exception)
        { throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The model pack is not supported by Supra.", exception); }

        // Automatic selection loads the CUDA driver to look for a device.
        TensorBackendKind backend = await Task.Run(() => ResolveBackend(_requestedBackend), cancellationToken).ConfigureAwait(false);
        if (!definition.SupportedBackends.Contains(backend))
        {
            throw new InferenceException(
                InferenceFailureCode.BackendUnavailable,
                $"Model pack '{definition.Key}' does not support backend '{backend}'.");
        }

        TensorModelPackStatus installed = await _packs.GetStatusAsync(model, cancellationToken).ConfigureAwait(false);
        if (installed.State != TensorModelPackState.Installed)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable,
                installed.Detail ?? $"Model pack '{definition.Key}' is not installed.");
        }

        await _lifetime.AcceptAsync(TensorEngineState.Loading, model, backend,
            token => LoadCoreAsync(definition, backend, token), cancellationToken).ConfigureAwait(false);
    }

    private async Task<TensorEngineStatus> LoadCoreAsync(
        TensorModelPackDefinition definition, TensorBackendKind backend, CancellationToken cancellationToken)
    {
        using IDisposable loading = await _lifetime.EnterLoadAsync(cancellationToken).ConfigureAwait(false);
        TensorModelPackLease lease;
        try
        {
            lease = await _packs.AcquireAsync(definition.Key, cancellationToken).ConfigureAwait(false);
        }
        catch (TensorModelPackException exception)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
        }

        ISupraImageModel? loadedModel = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            loadedModel = await _loader.LoadAsync(lease, backend, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _lifetime.MarkLoaded(definition.Key, backend);
            _model = loadedModel;
            _modelLease = lease;
            _loadedBackend = backend;
            _definition = definition;
            loadedModel = null;
            return new TensorEngineStatus(definition.Key, backend, TensorEngineState.Loaded, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            loadedModel?.Dispose();
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            loadedModel?.Dispose();
            await lease.DisposeAsync().ConfigureAwait(false);
            if (exception is InferenceException) { throw; }
            throw new InferenceException(
                exception switch
                {
                    OutOfMemoryException => InferenceFailureCode.CapacityExhausted,
                    InvalidDataException => InferenceFailureCode.ModelCorrupt,
                    _ => InferenceFailureCode.BackendUnavailable
                },
                $"TensorSharp could not load Supra2-IMG on backend '{backend}'.",
                exception);
        }
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        TensorModelObservation current = _lifetime.Observation;
        return _lifetime.AcceptAsync(TensorEngineState.Unloading, current.Model, current.Backend, async token =>
        {
            await ReleaseModelAsync(token).ConfigureAwait(false);
            return new TensorEngineStatus(null, null, TensorEngineState.Unloaded, DateTimeOffset.UtcNow);
        }, cancellationToken, teardown: true);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_lifetime.StopAccepted)
        {
            return Task.CompletedTask;
        }

        TensorModelObservation current = _lifetime.Observation;
        return _lifetime.AcceptAsync(TensorEngineState.Stopping, current.Model, current.Backend, async token =>
        {
            await ReleaseModelAsync(token).ConfigureAwait(false);
            _disposed = true;
            if (_ownsCoordinator)
            {
                _coordinator.Dispose();
            }

            _lifetime.Dispose();
            return new TensorEngineStatus(null, null, TensorEngineState.Stopped, DateTimeOffset.UtcNow);
        }, cancellationToken, teardown: true, stops: true);
    }

    /// <summary>Performs Stop and completes after native work has released model memory.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _lifetime.WhenIdleAsync().ConfigureAwait(false);
    }

    /// <summary>Waits for active requests, or cancels them when the token is cancelled, then releases the model.</summary>
    private async Task ReleaseModelAsync(CancellationToken urgency)
    {
        using IDisposable unloading = await _lifetime.EnterUnloadAsync(urgency).ConfigureAwait(false);
        _model?.Dispose();
        _model = null;
        if (_modelLease is not null)
        {
            await _modelLease.DisposeAsync().ConfigureAwait(false);
            _modelLease = null;
        }

        _loadedBackend = null;
        _definition = null;
        _lifetime.MarkUnloaded();
    }

    private static void ValidateRequest(ImageGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.References);
        if (request.References.Count > 0)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature,
                "Supra2-IMG does not support image references.");

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "An image prompt is required.");
        }

        if (!string.IsNullOrEmpty(request.NegativePrompt))
        {
            throw new InferenceException(
                InferenceFailureCode.UnsupportedFeature,
                "Supra2-IMG does not support a negative prompt.");
        }

        int width = request.Width ?? SupraDiT.ImageSize;
        int height = request.Height ?? SupraDiT.ImageSize;
        if (width != SupraDiT.ImageSize || height != SupraDiT.ImageSize)
        {
            throw new InferenceException(
                InferenceFailureCode.UnsupportedFeature,
                $"Supra2-IMG supports only {SupraDiT.ImageSize}x{SupraDiT.ImageSize} images.");
        }

        if (request.Count is < 1 or > MaximumCount)
        {
            throw new InferenceException(
                InferenceFailureCode.InvalidRequest,
                $"Image count must be between 1 and {MaximumCount}.");
        }

        if (request.Steps is < 1 or > 200)
        {
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "Steps must be between 1 and 200.");
        }

        if (request.GuidanceScale is < 0d or > 20d or double.NaN)
        {
            throw new InferenceException(
                InferenceFailureCode.InvalidRequest,
                "Guidance scale must be a finite value between 0 and 20.");
        }
    }

    private async Task ValidateModelIdAsync(string? modelId, CancellationToken cancellationToken)
    {
        bool known = _definition is not null ? modelId == _definition.Key.ModelId
            : (await _packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false)).Any(definition => definition.Key.ModelId == modelId
                && definition.Capability == TensorCapability.Image && definition.RuntimeArchitecture == TensorImageModels.SupraRuntimeArchitecture);
        if (modelId is not null && !known)
        {
            throw new InferenceException(
                InferenceFailureCode.ModelUnavailable,
                $"Model '{modelId}' is not bound to this Supra2-IMG runtime.");
        }
    }

    private static TensorBackendKind ResolveBackend(TensorBackendKind backend) => backend switch
    {
        TensorBackendKind.Auto when CudaBackend.IsAvailable() => TensorBackendKind.Cuda,
        TensorBackendKind.Auto => TensorBackendKind.Cpu,
        _ => backend
    };

    private static string RuntimeVersion() =>
        typeof(DirectContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(DirectContext).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed || _lifetime.StopAccepted, this);
}
