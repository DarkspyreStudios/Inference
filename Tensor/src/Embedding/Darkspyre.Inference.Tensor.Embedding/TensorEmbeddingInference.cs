using System.Diagnostics;
using System.Collections.Frozen;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;
using TensorSharp.Models.Embeddings;

namespace Darkspyre.Inference.Tensor.Embedding;

/// <summary>One host-owned resident embedding model. No implicit download, model cache or background service.</summary>
public sealed class TensorEmbeddingInference : ITensorEmbeddingInference
{
    private readonly ITensorModelPackManager _packs;
    private readonly IEmbeddingModelLoader _loader;
    private readonly TensorModelLifetime _lifetime;
    private readonly GgmlRuntimeOwner? _runtimeOwner;
    private IEmbeddingModel? _model;
    private GgmlRuntimeOwner.ModelLease? _runtimeLease;
    private TensorBackendKind _backend;
    private TensorModelPackLease? _lease;
    private TensorModelPackDefinition? _definition;
    private TensorEmbeddingArchitecture? _architecture;
    private readonly object _stopGate = new();
    private volatile bool _stopRequested;
    private volatile bool _disposed;
    private volatile bool _releaseRequired;
    private Task? _stopAttempt;
    private Task? _disposal;

    /// <summary>
    /// An engine on <paramref name="backend"/>. <see cref="TensorBackendKind.Cpu"/> runs the managed encoder; Auto and
    /// the GGML backends borrow <paramref name="runtimeOwner"/>, or the generation's owner when it is null.
    /// </summary>
    public TensorEmbeddingInference(ITensorModelPackManager packs, TensorBackendKind backend = TensorBackendKind.Auto,
        GgmlRuntimeOwner? runtimeOwner = null)
        : this(packs, new EmbeddingModelLoader(), backend, runtimeOwner) { }

    internal TensorEmbeddingInference(ITensorModelPackManager packs, IEmbeddingModelLoader loader,
        TensorBackendKind backend = TensorBackendKind.Auto, GgmlRuntimeOwner? runtimeOwner = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(loader);
        if (backend != TensorBackendKind.Cpu && !GgmlRuntimeOwner.IsGgml(backend))
            throw new ArgumentOutOfRangeException(nameof(backend), "Embeddings run on managed CPU or a GGML backend.");
        _packs = packs; _loader = loader; RequestedBackend = backend;
        _runtimeOwner = backend == TensorBackendKind.Cpu ? null : runtimeOwner ?? GgmlRuntimeOwner.GetOrCreate();
        _lifetime = new(this);
    }

    /// <summary>The embedding implementation's descriptor from <see cref="EmbeddingSetup.Definition"/>.</summary>
    public InferenceImplementationDescriptor Descriptor => EmbeddingSetup.Definition.Implementation;

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
                pack.Key.ModelId == modelId && pack.Capability == TensorCapability.Embedding)
                ?? throw new InferenceException(InferenceFailureCode.ModelUnavailable, "No embedding pack with that model ID is registered.");
        TensorEmbeddingArchitecture architecture = Architecture(definition);
        return new()
        {
            Modalities = new[] { EmbeddingModality.Text }.ToFrozenSet(),
            NativeDimensions = architecture.NativeDimensions,
            SupportedDimensions = architecture.SupportedDimensions,
            MaximumInputTokens = architecture.MaximumInputTokens,
            ModelFingerprint = architecture.Fingerprint(definition),
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
        TensorEmbeddingArchitecture architecture = Architecture(definition);
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
                token => LoadCoreAsync(definition, architecture, token), cancellationToken);
        }
        await accepted.ConfigureAwait(false);
    }

    private async Task<TensorEngineStatus> LoadCoreAsync(TensorModelPackDefinition definition, TensorEmbeddingArchitecture architecture,
        CancellationToken cancellationToken)
    {
        using IDisposable loading = await _lifetime.EnterLoadAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfReleaseRequired();
        if (_model is not null || _lease is not null || _runtimeLease is not null)
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The previous embedding model ownership must be released before loading another model.");
        TensorModelPackLease lease;
        try { lease = await _packs.AcquireAsync(definition.Key, cancellationToken).ConfigureAwait(false); }
        catch (TensorModelPackException) { throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The embedding pack must be installed and verified before Load."); }
        _lease = lease;
        try
        {
            if (lease.Key != definition.Key) throw new InvalidDataException("The acquired embedding pack identifies another model.");
            TensorBackendKind backend = RequestedBackend;
            if (_runtimeOwner is not null)
            {
                // The owner chooses the actual GGML backend for Auto; the pack must support what it chose.
                _runtimeLease = await _runtimeOwner.AcquireModelAsync(RequestedBackend, cancellationToken).ConfigureAwait(false);
                backend = _runtimeLease.Backend;
                if (!definition.SupportedBackends.Contains(backend))
                    throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The initialized backend is incompatible with this embedding pack.");
            }
            _model = await _loader.LoadAsync(definition, architecture, lease, backend, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (_model.Architecture != architecture.GgufArchitecture || _model.Dimensions != architecture.NativeDimensions)
                throw new InvalidDataException("The embedding model's architecture or vector size does not match its pack.");
            _definition = definition; _architecture = architecture; _backend = backend;
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
    /// name the resident model.
    /// </summary>
    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); ArgumentNullException.ThrowIfNull(request);
        await using TensorModelLifetime.ExecutionLease execution = await _lifetime.EnterExecutionAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfStopped();
        ThrowIfReleaseRequired();
        TensorModelPackDefinition definition = _definition!;
        if (request.ModelId != definition.Key.ModelId)
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The request does not identify the resident embedding model.");
        long started = Stopwatch.GetTimestamp();
        using IDisposable? runtimeUse = _runtimeLease is not null ? _runtimeOwner!.EnterExecution() : null;
        EmbeddingRun run;
        try { run = await EmbeddingModelRuntime.EmbedAsync(_model!, _architecture!, request, execution.CancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException and not InferenceException and not EmbeddingInputTooLongException)
        {
            throw new InferenceException(exception is OutOfMemoryException ? InferenceFailureCode.CapacityExhausted : InferenceFailureCode.ExecutionFailed,
                "The embedding model could not embed the supplied inputs.", exception);
        }
        return new()
        {
            Vectors = run.Vectors,
            Dimensions = run.Dimensions,
            ModelFingerprint = _architecture!.Fingerprint(definition),
            TruncatedInputs = run.TruncatedInputs,
            Usage = new(run.InputTokens),
            Provenance = new()
            {
                ImplementationId = Descriptor.Id,
                ModelId = definition.Key.ModelId,
                ModelRevision = definition.Key.Revision,
                Runtime = "TensorSharp",
                RuntimeVersion = typeof(EmbeddingModel).Assembly.GetName().Version?.ToString(),
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
            // A live model keeps its dependent runtime and artifact leases when release fails.
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
        try
        {
            _runtimeLease?.Dispose();
            _runtimeLease = null;
        }
        catch (Exception error) { failures.Add(error); }
        _releaseRequired = failures.Count != 0;
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
        _definition = null; _architecture = null;
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

    private static TensorEmbeddingArchitecture Architecture(TensorModelPackDefinition definition)
    {
        try { return EmbeddingCompatibility.Validate(definition); }
        catch (ArgumentException) { throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "This is not a compatible embedding pack."); }
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopRequested || _disposed, this);

    private void ThrowIfReleaseRequired()
    {
        if (_releaseRequired)
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The embedding model still owns resources after failed cleanup.");
    }
}
