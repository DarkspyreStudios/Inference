using System.Diagnostics;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Models.Embeddings;

namespace Darkspyre.Inference.Tensor.Embedding;

/// <summary>One host-owned resident embedding model. No implicit download, model cache or background service.</summary>
public sealed class TensorEmbeddingInference : ITensorEmbeddingInference
{
    private readonly ITensorModelPackManager _packs;
    private readonly IEmbeddingModelLoader _loader;
    private readonly TensorModelLifetime _lifetime;
    private IEmbeddingModel? _model;
    private TensorModelPackLease? _lease;
    private TensorModelPackDefinition? _definition;
    private TensorEmbeddingArchitecture? _architecture;

    public TensorEmbeddingInference(ITensorModelPackManager packs, TensorBackendKind backend = TensorBackendKind.Auto)
        : this(packs, new EmbeddingModelLoader(), backend) { }

    internal TensorEmbeddingInference(ITensorModelPackManager packs, IEmbeddingModelLoader loader, TensorBackendKind backend = TensorBackendKind.Auto)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(loader);
        _packs = packs; _loader = loader; RequestedBackend = backend;
        _lifetime = new(this);
    }

    public InferenceImplementationDescriptor Descriptor => EmbeddingSetup.Definition.Implementation;

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

    public TensorBackendKind RequestedBackend { get; }

    /// <summary>Start after Stop is an allowed no-op; it does not reset a stopped instance.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lifetime.StopAccepted) return Task.CompletedTask;
        return _lifetime.AcceptAsync(TensorEngineState.Starting, null, null,
            _ => Task.FromResult(new TensorEngineStatus(null, null, TensorEngineState.Started, DateTimeOffset.UtcNow)),
            cancellationToken);
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
            NativeDimensions = architecture.NativeDimensions,
            SupportedDimensions = architecture.SupportedDimensions,
            MaximumInputTokens = architecture.MaximumInputTokens,
            ModelFingerprint = architecture.Fingerprint(definition),
        };
    }

    public async Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); ArgumentNullException.ThrowIfNull(model);
        TensorModelPackDefinition definition;
        try { definition = await _packs.GetRequiredDefinitionAsync(model, cancellationToken).ConfigureAwait(false); }
        catch (KeyNotFoundException) { throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The embedding pack is not registered."); }
        if (definition.Key != model) throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The pack manager returned another model.");
        TensorEmbeddingArchitecture architecture = Architecture(definition);
        if (RequestedBackend is not (TensorBackendKind.Auto or TensorBackendKind.Cpu))
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "Embeddings run on the managed CPU backend only.");
        TensorModelPackStatus installed = await _packs.GetStatusAsync(model, cancellationToken).ConfigureAwait(false);
        if (installed.State != TensorModelPackState.Installed)
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The embedding pack must be installed and verified before Load.");
        await _lifetime.AcceptAsync(TensorEngineState.Loading, model, TensorBackendKind.Cpu,
            token => LoadCoreAsync(definition, architecture, token), cancellationToken).ConfigureAwait(false);
    }

    private async Task<TensorEngineStatus> LoadCoreAsync(TensorModelPackDefinition definition, TensorEmbeddingArchitecture architecture,
        CancellationToken cancellationToken)
    {
        using IDisposable loading = await _lifetime.EnterLoadAsync(cancellationToken).ConfigureAwait(false);
        TensorModelPackLease lease;
        try { lease = await _packs.AcquireAsync(definition.Key, cancellationToken).ConfigureAwait(false); }
        catch (TensorModelPackException) { throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The embedding pack must be installed and verified before Load."); }
        IEmbeddingModel? loaded = null;
        try
        {
            if (lease.Key != definition.Key) throw new InvalidDataException("The acquired embedding pack identifies another model.");
            loaded = await _loader.LoadAsync(definition, architecture, lease, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _model = loaded; _lease = lease; _definition = definition; _architecture = architecture;
            _lifetime.MarkLoaded(definition.Key, TensorBackendKind.Cpu);
            return new TensorEngineStatus(definition.Key, TensorBackendKind.Cpu, TensorEngineState.Loaded, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            loaded?.Dispose();
            await lease.DisposeAsync().ConfigureAwait(false);
            _model = null; _lease = null; _definition = null; _architecture = null;
            if (exception is OperationCanceledException or InferenceException or ObjectDisposedException) throw;
            throw new InferenceException(exception switch
            {
                OutOfMemoryException => InferenceFailureCode.CapacityExhausted,
                InvalidDataException or NotSupportedException => InferenceFailureCode.ModelCorrupt,
                _ => InferenceFailureCode.BackendUnavailable
            }, "The embedding model could not be loaded.", exception);
        }
    }

    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); ArgumentNullException.ThrowIfNull(request);
        await using TensorModelLifetime.ExecutionLease execution = await _lifetime.EnterExecutionAsync(cancellationToken).ConfigureAwait(false);
        TensorModelPackDefinition definition = _definition!;
        if (request.ModelId != definition.Key.ModelId)
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The request does not identify the resident embedding model.");
        long started = Stopwatch.GetTimestamp();
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
                Backend = TensorBackendKind.Cpu.ToString(),
                Elapsed = Stopwatch.GetElapsedTime(started)
            }
        };
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        TensorModelObservation current = _lifetime.Observation;
        return _lifetime.AcceptAsync(TensorEngineState.Unloading, current.Model, current.Backend, async token =>
        {
            await ReleaseModelAsync(token).ConfigureAwait(false);
            return new TensorEngineStatus(null, null, TensorEngineState.Unloaded, DateTimeOffset.UtcNow);
        }, cancellationToken, teardown: true);
    }

    /// <summary>Waits for active requests, or cancels them when the token is cancelled, then releases the model.</summary>
    private async Task ReleaseModelAsync(CancellationToken token)
    {
        using IDisposable unloading = await _lifetime.EnterUnloadAsync(token).ConfigureAwait(false);
        _model?.Dispose();
        _model = null; _definition = null; _architecture = null;
        if (_lease is not null) await _lease.DisposeAsync().ConfigureAwait(false);
        _lease = null;
        _lifetime.MarkUnloaded();
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_lifetime.StopAccepted) return Task.CompletedTask;
        TensorModelObservation current = _lifetime.Observation;
        return _lifetime.AcceptAsync(TensorEngineState.Stopping, current.Model, current.Backend, async token =>
        {
            await ReleaseModelAsync(token).ConfigureAwait(false);
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

    private static TensorEmbeddingArchitecture Architecture(TensorModelPackDefinition definition)
    {
        try { return EmbeddingCompatibility.Validate(definition); }
        catch (ArgumentException) { throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "This is not a compatible embedding pack."); }
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_lifetime.StopAccepted, this);
}
