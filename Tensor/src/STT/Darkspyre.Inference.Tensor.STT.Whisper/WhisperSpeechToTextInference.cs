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
    private readonly TensorModelLifetime _lifetime = new();
    private readonly Lock _stopGate = new();
    private Task? _stopTask;
    private volatile bool _stopping;
    private IWhisperModel? _model;
    private TensorModelPackLease? _lease;

    public WhisperSpeechToTextInference(ITensorModelPackManager packs, TensorBackendKind backend = TensorBackendKind.Auto)
        : this(packs, new WhisperLoader(), backend) { }

    internal WhisperSpeechToTextInference(ITensorModelPackManager packs, IWhisperLoader loader, TensorBackendKind backend = TensorBackendKind.Auto)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(loader);
        _packs = packs; _loader = loader; RequestedBackend = backend;
    }

    public InferenceImplementationDescriptor Descriptor => WhisperSetup.Definition.Implementation;
    public TensorModelObservation Observation => _lifetime.Observation;
    public TensorBackendKind RequestedBackend { get; }
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<SpeechModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SpeechModelInfo>>(Array.AsReadOnly(_packs.Definitions
            .Where(pack => pack.Capability == TensorCapability.SpeechTranscription
                && pack.RuntimeArchitecture == TensorSpeechModels.WhisperRuntimeArchitecture)
            .Select(TensorSpeechModels.Info).ToArray()));
    }

    public Task<SpeechToTextCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); cancellationToken.ThrowIfCancellationRequested();
        TensorModelKey? loaded = Observation.Model;
        IWhisperModel? current = _model;
        if (loaded is not null && current is not null && (modelId is null || loaded.ModelId == modelId))
            return Task.FromResult(current.Metadata.Capabilities);
        TensorModelPackDefinition? known = _packs.Definitions.FirstOrDefault(pack => pack.Key.ModelId == modelId
            && pack.RuntimeArchitecture == TensorSpeechModels.WhisperRuntimeArchitecture);
        SpeechModelInfo? info = known is null ? null : TensorSpeechModels.Info(known);
        return Task.FromResult(new SpeechToTextCapabilities(info?.EnglishOnly, info?.SupportsTranslationToEnglish,
            info?.EnglishOnly is bool english ? !english : null, info?.EnglishOnly == true ? Array.AsReadOnly<string>(["en"]) : null,
            WhisperPcm.MinimumRate, WhisperPcm.MaximumRate, WhisperPcm.MaximumChannels));
    }

    public async Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); ArgumentNullException.ThrowIfNull(model);
        TensorModelPackDefinition definition;
        try { definition = _packs.GetRequiredDefinition(model); }
        catch (KeyNotFoundException) { throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Whisper pack is not registered."); }
        if (definition.Key != model) throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The pack manager returned another model.");
        try { WhisperCompatibility.Validate(definition); }
        catch (ArgumentException) { throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "This is not a compatible Whisper pack."); }
        if (RequestedBackend is not (TensorBackendKind.Auto or TensorBackendKind.Cpu))
            throw new InferenceException(InferenceFailureCode.BackendUnavailable, "This Whisper adapter supplies CPU inference only.");
        using IDisposable loading = await _lifetime.EnterLoadAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfStopped();
        TensorModelPackLease lease;
        try { lease = await _packs.AcquireAsync(model, cancellationToken).ConfigureAwait(false); }
        catch (TensorModelPackException) { throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The Whisper pack must be installed and verified before Load."); }
        IWhisperModel? loaded = null;
        try
        {
            if (lease.Key != model) throw new InvalidDataException("The acquired Whisper pack identifies another model.");
            loaded = await _loader.LoadAsync(definition, lease, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfStopped();
            _model = loaded; _lease = lease;
            _lifetime.MarkLoaded(model, TensorBackendKind.Cpu);
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

    public async Task<SpeechTranscriptionResult> TranscribeAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Audio);
        await using TensorModelLifetime.ExecutionLease execution = await _lifetime.EnterExecutionAsync(cancellationToken).ConfigureAwait(false);
        if (request.ModelId is not null && request.ModelId != _lease!.Key.ModelId)
            throw new InferenceException(InferenceFailureCode.ModelUnavailable, "The request does not identify the resident Whisper model.");
        return await TranscribeCoreAsync(request.Audio, request.Options ?? new(), execution.CancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SpeechWindowTranscription> TranscribeStreamAsync(IAsyncEnumerable<SpeechAudioWindow> windows,
        SpeechToTextOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfStopped(); ArgumentNullException.ThrowIfNull(windows); ArgumentNullException.ThrowIfNull(options);
        await using TensorModelLifetime.ExecutionLease execution = await _lifetime.EnterExecutionAsync(cancellationToken).ConfigureAwait(false);
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
                Backend = TensorBackendKind.Cpu.ToString(),
                Elapsed = Stopwatch.GetElapsedTime(started)
            });
    }

    public Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        lock (_stopGate) { return _stopTask ?? UnloadCoreAsync(cancellationToken); }
    }

    private async Task UnloadCoreAsync(CancellationToken token)
    {
        using IDisposable unloading = await _lifetime.EnterUnloadAsync(token).ConfigureAwait(false);
        if (_model is not null) await _model.DisposeAsync().ConfigureAwait(false);
        _model = null;
        if (_lease is not null) await _lease.DisposeAsync().ConfigureAwait(false);
        _lease = null;
        _lifetime.MarkUnloaded();
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stopGate)
        {
            _stopping = true;
            return _stopTask ??= StopCoreAsync(cancellationToken);
        }
    }
    private async Task StopCoreAsync(CancellationToken token)
    {
        await UnloadCoreAsync(token).ConfigureAwait(false);
        _lifetime.Dispose();
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopping, this);
}
