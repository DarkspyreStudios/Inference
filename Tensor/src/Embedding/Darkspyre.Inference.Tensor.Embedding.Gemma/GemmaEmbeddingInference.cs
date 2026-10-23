using System.Diagnostics;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Tensor.Common;
using Microsoft.ML.OnnxRuntime;

namespace Darkspyre.Inference.Tensor.Embedding.Gemma;

/// <summary>One host-owned resident embedding model. No implicit download, model cache or background service.</summary>
public sealed class GemmaEmbeddingInference : ITensorEmbeddingInference
{
    private readonly ITensorModelPackManager _packs;
    private readonly IGemmaEmbeddingLoader _loader;
    private readonly TensorModelLifetime _lifetime;
    private IGemmaEmbeddingModel? _model;
    private TensorBackendKind _backend;
    private TensorModelPackLease? _lease;
    private TensorModelPackDefinition? _definition;
    private readonly object _stopGate = new();
    private volatile bool _stopRequested;
    private volatile bool _disposed;
    private volatile bool _releaseRequired;
    private Task? _stopAttempt;
    private Task? _disposal;

    /// <summary>Creates an unloaded EmbeddingGemma engine. Auto selects the packaged WebGPU provider; CPU is explicit.</summary>
    public GemmaEmbeddingInference(ITensorModelPackManager packs, TensorBackendKind backend = TensorBackendKind.Auto)
        : this(packs, new GemmaEmbeddingLoader(), backend) { }

    internal GemmaEmbeddingInference(ITensorModelPackManager packs, IGemmaEmbeddingLoader loader, TensorBackendKind backend)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(loader);
        if (backend is not (TensorBackendKind.Auto or TensorBackendKind.Cpu or TensorBackendKind.WebGpu))
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "EmbeddingGemma supports CPU and WebGPU.");
        _packs = packs; _loader = loader; RequestedBackend = backend;
        _lifetime = new(this);
    }

    /// <summary>The embedding implementation's descriptor from <see cref="GemmaEmbeddingSetup.Definition"/>.</summary>
    public InferenceImplementationDescriptor Descriptor => GemmaEmbeddingSetup.Definition.Implementation;

    /// <inheritdoc />
    public event EventHandler<TensorEngineStatus>? EngineStatusChanged
    {
        add => _lifetime.EngineStatusChanged += value;
        remove => _lifetime.EngineStatusChanged -= value;
    }

    /// <inheritdoc />
    public Task<TensorModelObservation> GetObservationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_lifetime.Observation);
    }

    /// <inheritdoc />
    public TensorBackendKind RequestedBackend { get; }

    /// <summary>Start after Stop is an allowed no-op; it does not reset a stopped instance.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stopGate)
        {
            if (_stopRequested) return Task.CompletedTask;
            ThrowIfReleaseRequired();
            return _lifetime.AcceptAsync(TensorEngineState.Starting, null, null,
                _ => Task.FromResult(new TensorEngineStatus(null, null, TensorEngineState.Started, DateTimeOffset.UtcNow)),
                cancellationToken);
        }
    }

    /// <summary>
    /// The capabilities of <paramref name="modelId"/>, or of the resident model when it is null. A model that is not
    /// resident is described from its registered pack.
    /// </summary>
    public async Task<EmbeddingCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); cancellationToken.ThrowIfCancellationRequested();
        TensorModelPackDefinition? definition = _definition;
        if (definition is null || (modelId is not null && definition.Key.ModelId != modelId))
            definition = (await _packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(pack =>
                pack.Key.ModelId == (modelId ?? GemmaEmbeddingModels.EmbeddingGemma2.Key.ModelId) && pack.Capability == TensorCapability.Embedding)
                ?? throw new InferenceException(InferenceFailureCode.ModelUnavailable, "No embedding pack with that model ID is registered.");
        GemmaEmbeddingModels.Validate(definition);
        return new()
        {
            Modalities = GemmaEmbeddingModels.Modalities,
            NativeDimensions = GemmaEmbeddingModels.Dimensions,
            SupportedDimensions = Array.AsReadOnly([GemmaEmbeddingModels.Dimensions]),
            MaximumInputTokens = GemmaEmbeddingModels.MaximumTokens,
            ModelFingerprint = GemmaEmbeddingModels.Fingerprint(definition),
        };
    }

    /// <inheritdoc />
    public async Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); ArgumentNullException.ThrowIfNull(model);
        TensorModelPackDefinition definition;
        try { definition = await _packs.GetRequiredDefinitionAsync(model, cancellationToken).ConfigureAwait(false); }
        catch (KeyNotFoundException) { throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The embedding pack is not registered."); }
        if (definition.Key != model) throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The pack manager returned another model.");
        GemmaEmbeddingModels.Validate(definition);
        if (RequestedBackend != TensorBackendKind.Auto && !definition.SupportedBackends.Contains(RequestedBackend))
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The embedding pack does not support the requested backend.");
        TensorModelPackStatus installed = await _packs.GetStatusAsync(model, cancellationToken).ConfigureAwait(false);
        if (installed.State != TensorModelPackState.Installed)
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The embedding pack must be installed and verified before Load.");
        Task accepted;
        lock (_stopGate)
        {
            ThrowIfStopped();
            ThrowIfReleaseRequired();
            accepted = _lifetime.AcceptAsync(TensorEngineState.Loading, model, RequestedBackend == TensorBackendKind.Auto ? null : RequestedBackend,
                token => LoadCoreAsync(definition, token), cancellationToken);
        }
        await accepted.ConfigureAwait(false);
    }

    private async Task<TensorEngineStatus> LoadCoreAsync(TensorModelPackDefinition definition,
        CancellationToken cancellationToken)
    {
        using IDisposable loading = await _lifetime.EnterLoadAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfReleaseRequired();
        if (_model is not null || _lease is not null)
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The previous embedding model ownership must be released before loading another model.");
        TensorModelPackLease lease;
        try { lease = await _packs.AcquireAsync(definition.Key, cancellationToken).ConfigureAwait(false); }
        catch (TensorModelPackException error)
        {
            throw new InferenceException(InferenceFailureCode.ModelUnavailable,
                $"The embedding pack must be installed and verified before Load. {error.Message}", error);
        }
        _lease = lease;
        try
        {
            if (lease.Key != definition.Key) throw new InvalidDataException("The acquired embedding pack identifies another model.");
            TensorBackendKind backend = GemmaOnnxBackend.Resolve(RequestedBackend);
            if (!definition.SupportedBackends.Contains(backend))
                throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The EmbeddingGemma pack does not support the selected backend.");
            _model = await _loader.LoadAsync(lease, backend, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _definition = definition; _backend = backend;
            _lifetime.MarkLoaded(definition.Key, backend);
            return new TensorEngineStatus(definition.Key, backend, TensorEngineState.Loaded, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            try { await ReleaseOwnedModelAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException(exception, cleanup); }
            if (exception is OperationCanceledException or InferenceException or ObjectDisposedException) throw;
            throw new InferenceException(exception switch
            {
                OutOfMemoryException => InferenceFailureCode.CapacityExhausted,
                InvalidDataException or NotSupportedException => InferenceFailureCode.ModelCorrupt,
                _ => InferenceFailureCode.BackendUnavailable
            }, "The embedding model could not be loaded.", exception);
        }
    }

    /// <summary>
    /// Embeds the request's inputs with the resident model and returns the vectors, usage and provenance. The request must
    /// name the resident model. A query request runs before waiting document requests, and a document request lets a
    /// waiting query go before each of its inputs is prepared and before each of its ONNX runs, so a query waits for at most
    /// one run.
    /// </summary>
    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); ArgumentNullException.ThrowIfNull(request);
        await using TensorModelLifetime.ExecutionLease execution = await _lifetime.EnterExecutionAsync(
            request.Kind == EmbeddingInputKind.Query, cancellationToken).ConfigureAwait(false);
        ThrowIfStopped();
        ThrowIfReleaseRequired();
        TensorModelPackDefinition definition = _definition!;
        if (request.ModelId != definition.Key.ModelId)
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The request does not identify the resident embedding model.");
        long started = Stopwatch.GetTimestamp();
        GemmaEmbeddingRun run;
        try { run = await _model!.EmbedAsync(request, execution).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException and not InferenceException and not EmbeddingInputTooLongException)
        {
            throw new InferenceException(exception is OutOfMemoryException ? InferenceFailureCode.CapacityExhausted : InferenceFailureCode.ExecutionFailed,
                "The embedding model could not embed the supplied inputs.", exception);
        }
        return new()
        {
            Vectors = run.Vectors,
            Dimensions = GemmaEmbeddingModels.Dimensions,
            ModelFingerprint = GemmaEmbeddingModels.Fingerprint(definition),
            TruncatedInputs = run.TruncatedInputs,
            Usage = new(run.InputTokens),
            Provenance = new()
            {
                ImplementationId = Descriptor.Id,
                ModelId = definition.Key.ModelId,
                ModelRevision = definition.Key.Revision,
                Runtime = "ONNX Runtime",
                RuntimeVersion = typeof(InferenceSession).Assembly.GetName().Version?.ToString(),
                Backend = _backend.ToString(),
                Elapsed = Stopwatch.GetElapsedTime(started)
            }
        };
    }

    /// <inheritdoc />
    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        lock (_stopGate)
        {
            ThrowIfStopped();
            TensorModelObservation current = _lifetime.Observation;
            return _lifetime.AcceptAsync(TensorEngineState.Unloading, current.Model, current.Backend, async token =>
            {
                await ReleaseModelAsync(token).ConfigureAwait(false);
                return new TensorEngineStatus(null, null, TensorEngineState.Unloaded, DateTimeOffset.UtcNow);
            }, cancellationToken, teardown: true);
        }
    }

    /// <summary>Waits for active requests, or cancels them when the token is cancelled, then releases the model.</summary>
    private async Task ReleaseModelAsync(CancellationToken token)
    {
        using IDisposable unloading = await _lifetime.EnterUnloadAsync(token).ConfigureAwait(false);
        await ReleaseOwnedModelAsync().ConfigureAwait(false);
        _lifetime.MarkUnloaded();
    }

    private async Task ReleaseOwnedModelAsync()
    {
        try
        {
            // A live model keeps its dependent artifact lease when release fails.
            _model?.Dispose();
            _model = null;
        }
        catch
        {
            _releaseRequired = true;
            throw;
        }

        List<Exception> failures = [];
        try
        {
            if (_lease is not null) await _lease.DisposeAsync().ConfigureAwait(false);
            _lease = null;
        }
        catch (Exception error) { failures.Add(error); }
        _releaseRequired = failures.Count != 0;
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
        _definition = null;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stopGate)
        {
            if (_disposed || _stopAttempt is { IsCompleted: false }) return Task.CompletedTask;
            _stopRequested = true;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopAttempt = completion.Task;
            TensorModelObservation current = _lifetime.Observation;
            try
            {
                return _lifetime.AcceptAsync(TensorEngineState.Stopping, current.Model, current.Backend,
                    token => StopCoreAsync(completion, token), cancellationToken, teardown: true);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
                throw;
            }
        }
    }

    private async Task<TensorEngineStatus> StopCoreAsync(TaskCompletionSource completion, CancellationToken token)
    {
        try
        {
            await ReleaseModelAsync(token).ConfigureAwait(false);
            _lifetime.Dispose();
            _disposed = true;
            completion.TrySetResult();
            return new TensorEngineStatus(null, null, TensorEngineState.Stopped, DateTimeOffset.UtcNow);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
            throw;
        }
    }

    /// <summary>Performs Stop and completes after native work has released model memory.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_stopGate) return new(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Task admission;
        Task cleanup;
        lock (_stopGate)
        {
            admission = _stopAttempt is null ? StopAsync() : Task.CompletedTask;
            cleanup = _stopAttempt!;
        }
        await admission.ConfigureAwait(false);
        await _lifetime.WhenIdleAsync().ConfigureAwait(false);
        await cleanup.ConfigureAwait(false);
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopRequested || _disposed, this);

    private void ThrowIfReleaseRequired()
    {
        if (_releaseRequired)
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The embedding model still owns resources after failed cleanup.");
    }
}
