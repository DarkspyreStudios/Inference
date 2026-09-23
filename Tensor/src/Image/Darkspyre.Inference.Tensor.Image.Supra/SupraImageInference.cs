using System.Diagnostics;
using System.Reflection;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Image;
using TensorSharp.Cuda;
using TensorSharp.Models.Direct;
using TensorSharp.Models.Media;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Image.Supra;

public sealed class SupraImageInference : ITensorImageInference
{
    private const int DefaultSteps = 50;
    private const float DefaultGuidance = 3f;
    private const int MaximumCount = 4;

    private readonly ITensorModelPackManager _packs;
    private readonly TensorModelPackDefinition _definition;
    private readonly TensorBackendKind _requestedBackend;
    private readonly TensorExecutionCoordinator _coordinator;
    private readonly bool _ownsCoordinator;
    private readonly TensorModelLifetime _lifetime = new();
    private TensorModelPackLease? _modelLease;
    private SupraImageModel? _model;
    private TensorBackendKind? _loadedBackend;
    private bool _disposed;

    public SupraImageInference(
        ITensorModelPackManager packs,
        TensorBackendKind backend = TensorBackendKind.Auto,
        TensorExecutionCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        _packs = packs;
        _definition = TensorImageModels.Supra2Image;

        _requestedBackend = backend;
        _coordinator = coordinator ?? new TensorExecutionCoordinator();
        _ownsCoordinator = coordinator is null;
    }

    public InferenceImplementationDescriptor Descriptor => SupraSetup.Definition.Implementation;

    public Task<ImageCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateModelId(modelId);
        return Task.FromResult(new ImageCapabilities
        {
            SupportedSizes = [(SupraDiT.ImageSize, SupraDiT.ImageSize)],
            SupportsNegativePrompt = false,
            SupportsSeed = true,
            SupportsSteps = true,
            SupportsGuidanceScale = true,
            MaximumImageCount = MaximumCount
        });
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
        CancellationToken executionToken = modelUse.CancellationToken;
        SupraImageModel model = _model
            ?? throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Supra image model was not loaded.");
        TensorBackendKind backend = _loadedBackend
            ?? throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The Supra backend was not loaded.");
        await using IAsyncDisposable execution = await _coordinator.AcquireAsync(
            _definition.Key,
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
                byte[] rgb = model.Generate(
                    request.Prompt,
                    seed,
                    steps,
                    guidance,
                    (value, maximum) => progress?.Report(new ImageGenerationProgress(
                        ImageGenerationStage.Generating,
                        imageIndex * maximum + value,
                        request.Count * maximum)),
                    () => progress?.Report(new ImageGenerationProgress(ImageGenerationStage.Decoding)),
                    executionToken);
                byte[] png = MediaCodecs.Image.EncodePng(
                    rgb,
                    SupraDiT.ImageSize,
                    SupraDiT.ImageSize,
                    channels: 3);
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
                ModelId = _definition.Key.ModelId,
                ModelRevision = _definition.Key.Revision,
                Runtime = "TensorSharp",
                RuntimeVersion = RuntimeVersion(),
                Backend = backend.ToString(),
                Elapsed = Stopwatch.GetElapsedTime(started)
            }
        };
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public TensorModelObservation Observation => _lifetime.Observation;
    public TensorBackendKind RequestedBackend => _requestedBackend;

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        using IDisposable unloading = await _lifetime.EnterUnloadAsync(cancellationToken).ConfigureAwait(false);
        _model?.Dispose();
        _model = null;
        if (_modelLease is not null)
        {
            await _modelLease.DisposeAsync().ConfigureAwait(false);
            _modelLease = null;
        }

        _loadedBackend = null;
        _lifetime.MarkUnloaded();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await UnloadAsync(cancellationToken).ConfigureAwait(false);
        _disposed = true;
        _lifetime.Dispose();
        if (_ownsCoordinator)
        {
            _coordinator.Dispose();
        }

    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    public async Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(model);
        if (model != _definition.Key)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, $"Supra does not support model '{model}'.");
        }

        TensorModelPackDefinition definition;
        try
        {
            definition = _packs.GetRequiredDefinition(model);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
        }

        if (definition.Capability != TensorCapability.Image
            || definition.RuntimeArchitecture != _definition.RuntimeArchitecture)
        {
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "The model pack is not supported by Supra.");
        }

        using IDisposable loading = await _lifetime.EnterLoadAsync(cancellationToken).ConfigureAwait(false);

        TensorBackendKind backend = ResolveBackend(_requestedBackend);
        if (!definition.SupportedBackends.Contains(backend))
        {
            throw new InferenceException(
                InferenceFailureCode.BackendUnavailable,
                $"Model pack '{_definition.Key}' does not support backend '{backend}'.");
        }

        TensorModelPackLease lease;
        try
        {
            lease = await _packs.AcquireAsync(model, cancellationToken).ConfigureAwait(false);
        }
        catch (TensorModelPackException exception)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, exception.Message, exception);
        }

        SupraImageModel? loadedModel = null;
        try
        {
            SupraTextTokenizer tokenizer = await SupraTextTokenizer.CreateAsync(
                lease.GetRequiredArtifact("text-tokenizer"),
                cancellationToken).ConfigureAwait(false);
            using SafetensorsFile textWeights = await SafetensorsFile.OpenAsync(
                lease.GetRequiredArtifact("text-model"),
                cancellationToken).ConfigureAwait(false);
            using TorchStateDictionaryFile ditWeights = await TorchStateDictionaryFile.OpenAsync(
                lease.GetRequiredArtifact("supra-model"),
                cancellationToken).ConfigureAwait(false);
            using SafetensorsFile vaeWeights = await SafetensorsFile.OpenAsync(
                lease.GetRequiredArtifact("vae-model"),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            loadedModel = new SupraImageModel(
                tokenizer,
                textWeights,
                ditWeights,
                vaeWeights,
                backend == TensorBackendKind.Cuda);
            cancellationToken.ThrowIfCancellationRequested();
            _lifetime.MarkLoaded(model, backend);
            _model = loadedModel;
            _modelLease = lease;
            _loadedBackend = backend;
            loadedModel = null;
        }
        catch (OperationCanceledException)
        {
            loadedModel?.Dispose();
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not InferenceException)
        {
            loadedModel?.Dispose();
            await lease.DisposeAsync().ConfigureAwait(false);
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

    private void ValidateRequest(ImageGenerationRequest request)
    {
        ValidateModelId(request.ModelId);
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

    private void ValidateModelId(string? modelId)
    {
        if (modelId is not null && !string.Equals(modelId, _definition.Key.ModelId, StringComparison.Ordinal))
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
