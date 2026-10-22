using System.Diagnostics;
using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;
using Whisper.net;

namespace Darkspyre.Inference.Tensor.STT.Whisper;

/// <summary>One host-owned resident Whisper model. No implicit download, model cache or background service.</summary>
public sealed class WhisperSpeechToTextInference : ITensorSpeechToTextInference
{
    private readonly ITensorModelPackManager _packs;
    private readonly IWhisperLoader _loader;
    private readonly TensorModelLifetime _lifetime;
    private readonly int _gpuDevice;
    private IWhisperModel? _model;
    private TensorModelPackLease? _lease;

    /// <summary>Creates an unloaded Whisper instance. Auto prefers CUDA 12 and permits CPU fallback; Cpu disables GPU execution.</summary>
    /// <param name="packs">The host-owned installed model packs.</param>
    /// <param name="backend">Auto prefers CUDA 12; Cpu forces CPU execution; GgmlCuda requests CUDA execution.</param>
    /// <param name="gpuDevice">The zero-based GPU index passed to Whisper. The index uses the process's visible GPU list.</param>
    /// <gotcha>The native runtime is process-wide. An already-loaded CPU library remains in use until process exit. Observations and metrics report the backend selected by the native model.</gotcha>
    public WhisperSpeechToTextInference(ITensorModelPackManager packs, TensorBackendKind backend = TensorBackendKind.Auto, int gpuDevice = 0)
        : this(packs, new WhisperLoader(), backend, gpuDevice) { }

    internal WhisperSpeechToTextInference(ITensorModelPackManager packs, IWhisperLoader loader, TensorBackendKind backend = TensorBackendKind.Auto, int gpuDevice = 0)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentOutOfRangeException.ThrowIfNegative(gpuDevice);
        _packs = packs; _loader = loader; RequestedBackend = backend; _gpuDevice = gpuDevice;
        _lifetime = new(this);
    }

    /// <summary>Describes the Whisper implementation from its setup definition.</summary>
    public InferenceImplementationDescriptor Descriptor => WhisperSetup.Definition.Implementation;

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
        if (_lifetime.StopAccepted) return Task.CompletedTask;
        return _lifetime.AcceptAsync(TensorEngineState.Starting, null, null,
            _ => Task.FromResult(new TensorEngineStatus(null, null, TensorEngineState.Started, DateTimeOffset.UtcNow)),
            cancellationToken);
    }

    /// <summary>Lists the registered speech-transcription packs that use the Whisper runtime architecture.</summary>
    public async Task<IReadOnlyList<SpeechModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly((await _packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false))
            .Where(pack => pack.Capability == TensorCapability.SpeechTranscription
                && pack.RuntimeArchitecture == TensorSpeechModels.WhisperRuntimeArchitecture)
            .Select(TensorSpeechModels.Info).ToArray());
    }

    /// <summary>Returns the resident model's capabilities when it matches <paramref name="modelId"/>, or capabilities derived from the registered pack otherwise.</summary>
    public async Task<SpeechToTextCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); cancellationToken.ThrowIfCancellationRequested();
        TensorModelKey? loaded = _lifetime.Observation.Model;
        IWhisperModel? current = _model;
        if (loaded is not null && current is not null && (modelId is null || loaded.ModelId == modelId))
            return current.Metadata.Capabilities;
        TensorModelPackDefinition? known = (await _packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(pack => pack.Key.ModelId == modelId
            && pack.RuntimeArchitecture == TensorSpeechModels.WhisperRuntimeArchitecture);
        SpeechModelInfo? info = known is null ? null : TensorSpeechModels.Info(known);
        return new SpeechToTextCapabilities(info?.EnglishOnly, info?.SupportsTranslationToEnglish,
            info?.EnglishOnly is bool english ? (!english ? InferenceSupport.Supported : InferenceSupport.Unsupported) : null,
            info?.EnglishOnly == true ? Array.AsReadOnly<string>(["en"]) : null,
            WhisperPcm.MinimumRate, WhisperPcm.MaximumRate, WhisperPcm.MaximumChannels);
    }

    /// <inheritdoc />
    public async Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); ArgumentNullException.ThrowIfNull(model);
        TensorModelPackDefinition definition;
        try { definition = await _packs.GetRequiredDefinitionAsync(model, cancellationToken).ConfigureAwait(false); }
        catch (KeyNotFoundException) { throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Whisper pack is not registered."); }
        if (definition.Key != model) throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The pack manager returned another model.");
        try { WhisperCompatibility.Validate(definition); }
        catch (ArgumentException) { throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "This is not a compatible Whisper pack."); }
        if (RequestedBackend is not (TensorBackendKind.Auto or TensorBackendKind.Cpu or TensorBackendKind.GgmlCuda))
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "This Whisper adapter supports CPU and GGML CUDA execution.");
        TensorBackendKind backend = RequestedBackend == TensorBackendKind.Auto && !definition.SupportedBackends.Contains(TensorBackendKind.GgmlCuda)
            ? TensorBackendKind.Cpu : RequestedBackend;
        if (backend != TensorBackendKind.Auto && !definition.SupportedBackends.Contains(backend))
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "This Whisper pack does not support the selected backend.");
        TensorModelPackStatus installed = await _packs.GetStatusAsync(model, cancellationToken).ConfigureAwait(false);
        if (installed.State != TensorModelPackState.Installed)
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Whisper pack must be installed and verified before Load.");
        await _lifetime.AcceptAsync(TensorEngineState.Loading, model, backend == TensorBackendKind.Auto ? null : backend,
            token => LoadCoreAsync(definition, model, backend, token), cancellationToken).ConfigureAwait(false);
    }

    private async Task<TensorEngineStatus> LoadCoreAsync(TensorModelPackDefinition definition, TensorModelKey model, TensorBackendKind backend, CancellationToken cancellationToken)
    {
        using IDisposable loading = await _lifetime.EnterLoadAsync(cancellationToken).ConfigureAwait(false);
        TensorModelPackLease lease;
        try { lease = await _packs.AcquireAsync(model, cancellationToken).ConfigureAwait(false); }
        catch (TensorModelPackException) { throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Whisper pack must be installed and verified before Load."); }
        IWhisperModel? loaded = null;
        try
        {
            if (lease.Key != model) throw new InvalidDataException("The acquired Whisper pack identifies another model.");
            loaded = await _loader.LoadAsync(definition, lease, backend, _gpuDevice, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _model = loaded; _lease = lease;
            _lifetime.MarkLoaded(model, loaded.Backend);
            return new TensorEngineStatus(model, loaded.Backend, TensorEngineState.Loaded, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            if (loaded is not null) await loaded.DisposeAsync().ConfigureAwait(false);
            await lease.DisposeAsync().ConfigureAwait(false);
            _model = null; _lease = null;
            if (exception is OperationCanceledException or InferenceException or ObjectDisposedException) throw;
            throw new InferenceException(exception switch
            {
                OutOfMemoryException => InferenceFailureCode.CapacityExhausted,
                InvalidDataException or WhisperModelLoadException => InferenceFailureCode.ModelCorrupt,
                _ => InferenceFailureCode.BackendUnavailable
            }, "The Whisper model could not be loaded.", exception);
        }
    }

    /// <summary>Transcribes one audio buffer with the resident model. Throws when the request names a model other than the resident one.</summary>
    public async Task<SpeechTranscriptionResult> TranscribeAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Audio);
        await using TensorModelLifetime.ExecutionLease execution = await _lifetime.EnterExecutionAsync(urgent: false, cancellationToken).ConfigureAwait(false);
        if (request.ModelId is not null && request.ModelId != _lease!.Key.ModelId)
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The request does not identify the resident Whisper model.");
        return await TranscribeCoreAsync(request.Audio, request.Options ?? new(), execution.CancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SpeechWindowTranscription> TranscribeStreamAsync(IAsyncEnumerable<SpeechAudioWindow> windows,
        SpeechToTextOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); ArgumentNullException.ThrowIfNull(windows); ArgumentNullException.ThrowIfNull(options);
        await using TensorModelLifetime.ExecutionLease execution = await _lifetime.EnterExecutionAsync(urgent: false, cancellationToken).ConfigureAwait(false);
        _ = _model!.Metadata.Language(options);
        await foreach (SpeechAudioWindow window in windows.WithCancellation(execution.CancellationToken).ConfigureAwait(false))
        {
            execution.CancellationToken.ThrowIfCancellationRequested();
            if (window is null || string.IsNullOrWhiteSpace(window.Id) || window.Audio is null)
                throw new InferenceException(InferenceFailureCode.InvalidRequest, "Each audio window requires an identity and PCM audio.");
            yield return new(window.Id, await TranscribeCoreAsync(window.Audio, options, execution.CancellationToken).ConfigureAwait(false));
        }
    }

    private async Task<SpeechTranscriptionResult> TranscribeCoreAsync(SpeechAudio audio, SpeechToTextOptions options, CancellationToken token)
    {
        IWhisperModel model = _model!;
        TensorModelKey key = _lease!.Key;
        string language = model.Metadata.Language(options);
        long started = Stopwatch.GetTimestamp();
        WhisperTranscript transcript;
        try
        {
            float[] samples = WhisperPcm.Normalize(audio, token);
            transcript = samples.Length == 0 ? new([], null)
                : await model.TranscribeAsync(samples, language, options.TranslateToEnglish, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not InferenceException)
        {
            throw new InferenceException(exception is OutOfMemoryException ? InferenceFailureCode.CapacityExhausted : InferenceFailureCode.ExecutionFailed,
                "Whisper could not transcribe the supplied audio.", exception);
        }
        return new(string.Join(' ', transcript.Segments.Select(segment => segment.Text)).Trim(), transcript.Segments, transcript.Language,
            new()
            {
                ImplementationId = Descriptor.Id,
                ModelId = key.ModelId,
                ModelRevision = key.Revision,
                Runtime = "Whisper.net",
                RuntimeVersion = typeof(WhisperFactory).Assembly.GetName().Version?.ToString(),
                Backend = model.Backend.ToString(),
                Elapsed = Stopwatch.GetElapsedTime(started)
            });
    }

    /// <inheritdoc />
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
        if (_model is not null) await _model.DisposeAsync().ConfigureAwait(false);
        _model = null;
        if (_lease is not null) await _lease.DisposeAsync().ConfigureAwait(false);
        _lease = null;
        _lifetime.MarkUnloaded();
    }

    /// <inheritdoc />
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

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_lifetime.StopAccepted, this);
}
