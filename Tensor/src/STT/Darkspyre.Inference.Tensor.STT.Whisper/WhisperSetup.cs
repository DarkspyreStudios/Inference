using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.STT.Whisper;

public static class WhisperSetup
{
    public static InferenceSetupDefinition Definition { get; } = new(
        new("darkspyre.tensor.whisper", "Darkspyre Tensor Whisper", InferenceImplementationKind.Tensor, InferenceTask.SpeechTranscription),
        "Resident CPU speech transcription. Select and explicitly install a model; the host controls Load and Unload.");

    public static TensorSetupSession CreateSession(SetupConfigurationIdentity identity,
        ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        TensorSetupConfiguration? existing = null, ITensorSpeechToTextInference? instance = null,
        IReadOnlyList<TensorModelPackDefinition>? offeredModels = null, ITensorModelSource? source = null)
    {
        IReadOnlyList<TensorModelPackDefinition> models = offeredModels ?? TensorSpeechModels.Whisper;
        foreach (TensorModelPackDefinition model in models) WhisperCompatibility.Validate(model);
        return new(Definition, identity, target, models, packs, existing, instance, new WhisperBackendProbe(),
            source is null ? null : new WhisperModelResolver(source));
    }
}

/// <summary>Resolves published Whisper model content by immutable integrity identity and actual GGML header.
/// Unknown content stays Unverified; a filename or model-host tag is not architecture evidence.</summary>
public sealed class WhisperModelResolver : ITensorModelResolver
{
    public ITensorModelSource Source { get; }
    public WhisperModelResolver(ITensorModelSource source) { ArgumentNullException.ThrowIfNull(source); Source = source; }

    public async Task<TensorModelEvaluation> EvaluateAsync(TensorModelCandidate candidate, TensorBackendKind backend, CancellationToken cancellationToken = default)
        => (await ResolveCandidateAsync(candidate, backend, cancellationToken).ConfigureAwait(false)).Evaluation;

    public async Task<TensorModelResolution> ResolveAsync(TensorModelSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        TensorSourceResult<TensorModelCandidate> candidate = await Source.GetCandidateAsync(selection.Candidate, cancellationToken).ConfigureAwait(false);
        return candidate.State == TensorSourceState.Available && candidate.Value is not null
            ? await ResolveCandidateAsync(candidate.Value, selection.Backend, cancellationToken).ConfigureAwait(false)
            : new(new(TensorCompatibilityState.Unverified, candidate.Issues ?? [new("source-unavailable", "The model source is unavailable.")], [], candidate.State));
    }

    private async Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(candidate); token.ThrowIfCancellationRequested();
        static TensorModelResolution Failure(TensorCompatibilityState state, string code, string message) => new(new(state, [new(code, message)], []));
        if (backend is not (TensorBackendKind.Auto or TensorBackendKind.Cpu))
            return Failure(TensorCompatibilityState.Unsupported, "backend-unsupported", "This adapter supplies the CPU runtime only.");
        TensorSourceArtifact[] selected = candidate.Artifacts.Where(item => item.Path == candidate.Selection.Variant).ToArray();
        if (candidate.SourceId != Source.Id || selected.Length != 1)
            return Failure(TensorCompatibilityState.Unverified, "candidate-unavailable", "The selected source artifact is missing or ambiguous.");
        TensorSourceArtifact artifact = selected[0];
        TensorModelPackDefinition? known = TensorSpeechModels.KnownContent(artifact.Length, artifact.Sha256);
        if (known is null || string.IsNullOrWhiteSpace(candidate.License) || string.IsNullOrWhiteSpace(artifact.Provenance.License)
            || artifact.Provenance.Repository != candidate.Selection.Repository || artifact.Provenance.Revision != candidate.Selection.Revision)
            return Failure(TensorCompatibilityState.Unverified, "whisper-content-unverified", "Resolution currently verifies the twelve published Whisper weight sets by their pinned size and SHA-256; other content requires further compatibility evidence.");
        try
        {
            WhisperModelMetadata metadata = await new TensorMetadataReader(Source).ReadAsync(artifact, WhisperCompatibility.Inspect, token).ConfigureAwait(false);
            SpeechModelInfo expected = TensorSpeechModels.Info(known);
            if (metadata.EnglishOnly != expected.EnglishOnly || metadata.SupportsTranslation != expected.SupportsTranslationToEnglish)
                return Failure(TensorCompatibilityState.Unsupported, "whisper-header", "The checkpoint header disagrees with its declared weight identity.");
            TensorModelPackDefinition definition = TensorResolvedPack.Create(candidate, TensorCapability.SpeechTranscription,
                TensorSpeechModels.WhisperRuntimeArchitecture, known.SupportedBackends,
                [TensorResolvedPack.Artifact(artifact, "model", artifact.Path)], known);
            WhisperCompatibility.Validate(definition);
            return new(new(TensorCompatibilityState.Supported, [], [new(artifact.Path, "whisper-ggml",
                "Published content identity and actual GGML architecture/vocabulary header verified. Installation verifies all bytes; Load validates the native checkpoint.")]), definition);
        }
        catch (TensorSourceException exception) { return new(new(TensorCompatibilityState.Unverified, [new(exception.Code, exception.Message)], [], exception.State)); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
        { return Failure(TensorCompatibilityState.Unsupported, "whisper-header", "The model metadata is not supported by this Whisper adapter."); }
        catch (IOException) { return Failure(TensorCompatibilityState.Unverified, "whisper-metadata-incomplete", "The model header could not be inspected completely."); }
    }
}
