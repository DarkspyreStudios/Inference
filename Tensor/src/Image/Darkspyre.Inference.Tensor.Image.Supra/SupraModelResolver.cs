using System.Text.Json;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Image.Supra;

/// <summary>Resolves a compatible Supra2 DiT with the pipeline's pinned Flan-T5, tokenizer and VAE companions.</summary>
public sealed class SupraModelResolver : ITensorModelResolver
{
    public ITensorModelSource Source { get; }
    public SupraModelResolver(ITensorModelSource source) { ArgumentNullException.ThrowIfNull(source); Source = source; }
    public async Task<TensorModelEvaluation> EvaluateAsync(TensorModelCandidate candidate, TensorBackendKind backend, CancellationToken cancellationToken = default)
        => (await ResolveCandidateAsync(candidate, backend, cancellationToken).ConfigureAwait(false)).Evaluation;

    public async Task<TensorModelResolution> ResolveAsync(TensorModelSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        TensorSourceResult<TensorModelCandidate> candidate = await Source.GetCandidateAsync(selection.Candidate, cancellationToken).ConfigureAwait(false);
        return candidate.State == TensorSourceState.Available && candidate.Value is not null
            ? await ResolveCandidateAsync(candidate.Value, selection.Backend, cancellationToken).ConfigureAwait(false)
            : new(new(TensorCompatibilityState.Unverified, candidate.Issues ?? [new("source-unavailable", "The source could not provide a candidate.")], [], candidate.State));
    }

    private async Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        token.ThrowIfCancellationRequested();
        var evidence = new List<TensorCompatibilityEvidence>();
        TensorModelResolution Failure(TensorCompatibilityState state, string code, string message, TensorSourceState sourceState = TensorSourceState.Available) =>
            new(new(state, [new(code, message)], evidence.ToArray(), sourceState));
        if (candidate.SourceId != Source.Id || Source.Id != "hugging-face")
        { return Failure(TensorCompatibilityState.Unverified, "candidate-source", "This pipeline's pinned companions require the Hugging Face source."); }
        if (backend is not (TensorBackendKind.Auto or TensorBackendKind.Cpu or TensorBackendKind.Cuda))
        { return Failure(TensorCompatibilityState.Unsupported, "backend-unsupported", "This Supra pipeline supports CPU or CUDA."); }
        TensorSourceArtifact[] matches = candidate.Artifacts.Where(artifact => artifact.Path == candidate.Selection.Variant).ToArray();
        if (matches.Length != 1) { return Failure(TensorCompatibilityState.Unverified, "variant-missing", "The primary artifact is missing or ambiguous."); }
        try
        {
            TensorSourceArtifact primary = matches[0];
            ValidateIntegrity(primary);
            if (string.IsNullOrWhiteSpace(candidate.License) || primary.Provenance.Repository != candidate.Selection.Repository
                || primary.Provenance.Revision != candidate.Selection.Revision)
            { return Failure(TensorCompatibilityState.Unverified, "candidate-unpinned", "The selected checkpoint needs pinned provenance and license metadata."); }
            var reader = new TensorMetadataReader(Source);
            TorchStateDictionaryMetadata dit = await reader.ReadAsync(primary, stream => TorchStateDictionaryFile.InspectMetadata(stream), token).ConfigureAwait(false);
            SupraCheckpointSchema.ValidateDit(dit.Tensors, dit.IntegerValues);
            evidence.Add(new(primary.Path, "supra-dit", "Restricted PyTorch ZIP metadata matches the Supra2 DiT, patch/context configuration and unconditional tensor schema."));
            var artifacts = new List<TensorModelArtifactDefinition>
            { TensorResolvedPack.Artifact(primary, "supra-model", "supra/" + Path.GetFileName(primary.Path)) };
            foreach (TensorModelArtifactDefinition companion in TensorImageModels.Supra2Image.Artifacts.Where(artifact => artifact.Role != "supra-model"))
            {
                TensorArtifactProvenance pin = companion.Provenance!;
                TensorSourceResult<TensorModelCandidate> result = await Source.GetCandidateAsync(new(pin.Repository, pin.Revision, pin.Path), token).ConfigureAwait(false);
                if (result.State != TensorSourceState.Available || result.Value is null)
                {
                    return new(new(TensorCompatibilityState.Unverified, result.Issues ?? [new("companion-unavailable", "A required pinned pipeline companion is unavailable.")], evidence.ToArray(), result.State));
                }
                TensorSourceArtifact? artifact = result.Value.Artifacts.SingleOrDefault(item => item.Path == pin.Path);
                if (artifact is null) { return Failure(TensorCompatibilityState.Unverified, "companion-missing", "A required companion artifact is not listed."); }
                ValidateIntegrity(artifact);
                if (artifact.Provenance.Repository != pin.Repository || artifact.Provenance.Revision != pin.Revision
                    || artifact.Length != companion.Length || !string.Equals(artifact.Sha256, companion.Sha256, StringComparison.OrdinalIgnoreCase))
                { return Failure(TensorCompatibilityState.Unverified, "companion-integrity", "The source metadata does not match the pipeline's pinned companion."); }
                if (companion.Role is "text-model" or "vae-model")
                {
                    SafetensorsMetadata metadata = await reader.ReadAsync(artifact, stream => SafetensorsFile.InspectMetadata(stream), token).ConfigureAwait(false);
                    if (companion.Role == "text-model") { SupraCheckpointSchema.ValidateText(metadata.Tensors); }
                    else { SupraCheckpointSchema.ValidateVae(metadata.Tensors); }
                    evidence.Add(new(artifact.Path, companion.Role, "Safetensors header and required pipeline tensor schema verified."));
                }
                else
                {
                    // This pipeline fixes the already validated SentencePiece vocabulary by its immutable content hash.
                    // Arbitrary tokenizer replacements are not advertised as compatible with the pinned text encoder.
                    evidence.Add(new(artifact.Path, "text-tokenizer", "Pinned Flan-T5 SentencePiece tokenizer identity, size and SHA-256 match the supported vocabulary. Installation verifies its bytes."));
                }
                artifacts.Add(TensorResolvedPack.Artifact(artifact, companion.Role, companion.RelativePath));
            }
            TensorModelPackDefinition definition = TensorResolvedPack.Create(candidate, TensorCapability.Image,
                TensorImageModels.SupraRuntimeArchitecture, TensorImageModels.Supra2Image.SupportedBackends, artifacts, TensorImageModels.Supra2Image);
            SupraModelCompatibility.Validate(definition);
            return new(new(TensorCompatibilityState.Supported, [], evidence.ToArray()), definition);
        }
        catch (TensorSourceException exception)
        { return Failure(TensorCompatibilityState.Unverified, exception.Code, exception.Message, exception.State); }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or JsonException or OverflowException or ArgumentException or InvalidOperationException)
        { return Failure(TensorCompatibilityState.Unsupported, "supra-schema", "The checkpoint metadata does not satisfy this adapter's complete Supra2 pipeline schema."); }
        catch (IOException)
        { return Failure(TensorCompatibilityState.Unverified, "metadata-incomplete", "The complete checkpoint metadata could not be inspected."); }
    }

    private static void ValidateIntegrity(TensorSourceArtifact artifact)
    {
        if (artifact.Length is not > 0 || artifact.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(artifact.Provenance.License))
        { throw new TensorSourceException(TensorSourceState.Unverified, "pack-metadata-incomplete", "Every pipeline artifact requires a known size, SHA-256 and license."); }
    }
}
