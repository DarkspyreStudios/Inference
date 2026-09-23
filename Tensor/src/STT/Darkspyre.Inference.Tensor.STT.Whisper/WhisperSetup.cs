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
        IReadOnlyList<TensorModelPackDefinition>? offeredModels = null, ITensorModelSource? source = null, ITensorArtifactReader? reader = null)
    {
        IReadOnlyList<TensorModelPackDefinition> models = offeredModels ?? TensorSpeechModels.Whisper;
        foreach (TensorModelPackDefinition model in models) WhisperCompatibility.Validate(model);
        return new(Definition, identity, target, models, packs, existing, instance, new WhisperBackendProbe(),
            new WhisperModelResolver(source, reader));
    }
}

/// <summary>Resolves published Whisper model content by immutable integrity identity and actual GGML header.
/// Unknown content stays Unverified; a filename or model-host tag is not architecture evidence.</summary>
public sealed class WhisperModelResolver : TensorModelResolver
{
    public WhisperModelResolver(ITensorModelSource? source = null, ITensorArtifactReader? reader = null) : base(source, reader) { }
    public override IReadOnlyList<TensorArtifactRequirement> Requirements => [new("model", "*.bin")];

    public override async Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend,
        ITensorArtifactReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate); cancellationToken.ThrowIfCancellationRequested();
        static TensorModelResolution Failure(TensorCompatibilityState state, string code, string message) => new(new(state, [new(code, message)], []));
        if (backend is not (TensorBackendKind.Auto or TensorBackendKind.Cpu))
            return Failure(TensorCompatibilityState.Unsupported, "backend-unsupported", "This adapter supplies the CPU runtime only.");
        TensorSourceArtifact[] selected = candidate.Artifacts.Where(item => item.Path == candidate.Selection.Variant).ToArray();
        if (selected.Length != 1)
            return Failure(TensorCompatibilityState.Unverified, "candidate-unavailable", "The selected source artifact is missing or ambiguous.");
        TensorSourceArtifact artifact = selected[0];
        TensorModelPackDefinition? known = TensorSpeechModels.KnownContent(artifact.Length, artifact.Sha256);
        if (known is null || string.IsNullOrWhiteSpace(candidate.License) || string.IsNullOrWhiteSpace(artifact.Provenance.License))
            return Failure(TensorCompatibilityState.Unverified, "whisper-content-unverified", "Resolution currently verifies the twelve published Whisper weight sets by their pinned size and SHA-256; other content requires further compatibility evidence.");
        try
        {
            WhisperModelMetadata metadata = await new TensorMetadataReader(reader).ReadAsync(artifact, WhisperCompatibility.Inspect, cancellationToken).ConfigureAwait(false);
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
