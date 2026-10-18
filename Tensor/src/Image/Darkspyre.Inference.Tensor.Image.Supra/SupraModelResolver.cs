using System.Text.Json;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Image.Supra;

/// <summary>Resolves a compatible Supra2 DiT with the pipeline's pinned Flan-T5, tokenizer and VAE companions.</summary>
public sealed class SupraModelResolver : TensorModelResolver
{
    /// <summary>Creates a resolver over an optional repository source and artifact reader.</summary>
    public SupraModelResolver(ITensorModelSource? source = null, ITensorArtifactReader? reader = null) : base(source, reader) { }
    /// <inheritdoc />
    public override IReadOnlyList<TensorArtifactRequirement> Requirements => [new("supra-model", "*.pt"),
        .. TensorImageModels.Supra2Image.Artifacts.Where(item => item.Role != "supra-model").Select(item =>
            new TensorArtifactRequirement(item.Role, Path.GetFileName(item.RelativePath),
                new(item.Role, Source is not null && Source.Id == "hugging-face"
                    ? new TensorModelLocation.RepositoryArtifact(Source.Id, new(item.Provenance!.Repository, item.Provenance.Revision, item.Provenance.Path))
                    : new TensorModelLocation.RemoteFile(item.Sources[0]), item.Provenance!.License, item.Length, item.Sha256)))];

    /// <inheritdoc />
    public override async Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend,
        ITensorArtifactReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = new List<TensorCompatibilityEvidence>();
        TensorModelResolution Failure(TensorCompatibilityState state, string code, string message, TensorSourceState sourceState = TensorSourceState.Available) =>
            new(new(state, [new(code, message)], evidence.ToArray(), sourceState));
        if (backend is not (TensorBackendKind.Auto or TensorBackendKind.Cpu or TensorBackendKind.Cuda))
        { return Failure(TensorCompatibilityState.Unsupported, "backend-unsupported", "This Supra pipeline supports CPU or CUDA."); }
        TensorSourceArtifact[] matches = candidate.Artifacts.Where(artifact => artifact.Path == candidate.Selection.Variant).ToArray();
        if (matches.Length != 1) { return Failure(TensorCompatibilityState.Unverified, "variant-missing", "The primary artifact is missing or ambiguous."); }
        try
        {
            TensorSourceArtifact primary = matches[0];
            ValidateIntegrity(primary);
            if (string.IsNullOrWhiteSpace(candidate.License))
            { return Failure(TensorCompatibilityState.Unverified, "candidate-unpinned", "The selected checkpoint needs pinned provenance and license metadata."); }
            var metadataReader = new TensorMetadataReader(reader);
            TorchStateDictionaryMetadata dit = await metadataReader.ReadAsync(primary, stream => TorchStateDictionaryFile.InspectMetadata(stream), cancellationToken).ConfigureAwait(false);
            SupraCheckpointSchema.ValidateDit(dit.Tensors, dit.IntegerValues);
            evidence.Add(new(primary.Path, "supra-dit", "Restricted PyTorch ZIP metadata matches the Supra2 DiT, patch/context configuration and unconditional tensor schema."));
            var artifacts = new List<TensorModelArtifactDefinition>
            { TensorResolvedPack.Artifact(primary, "supra-model", "supra/" + Path.GetFileName(primary.Path)) };
            foreach (TensorModelArtifactDefinition companion in TensorImageModels.Supra2Image.Artifacts.Where(artifact => artifact.Role != "supra-model"))
            {
                candidate.ArtifactRoles.TryGetValue(companion.Role, out string? path);
                TensorSourceArtifact? artifact = candidate.Artifacts.SingleOrDefault(item => item.Path == path);
                if (artifact is null) { return Failure(TensorCompatibilityState.Unverified, "companion-missing", "A required companion artifact is not listed."); }
                ValidateIntegrity(artifact);
                if (artifact.Length != companion.Length || !string.Equals(artifact.Sha256, companion.Sha256, StringComparison.OrdinalIgnoreCase))
                { return Failure(TensorCompatibilityState.Unverified, "companion-integrity", "The source metadata does not match the pipeline's pinned companion."); }
                if (companion.Role is "text-model" or "vae-model")
                {
                    SafetensorsHeader header = await metadataReader.ReadAsync(artifact, stream => SafetensorsHeader.Read(stream), cancellationToken).ConfigureAwait(false);
                    // The schema reads the tensor table in TensorSharp's shape, the one an installed load opens.
                    Dictionary<string, SafetensorTensorInfo> tensors = header.Tensors.ToDictionary(entry => entry.Key, entry => new SafetensorTensorInfo
                    {
                        Name = entry.Key,
                        Shape = entry.Value.Shape,
                        Dtype = Enum.Parse<SafetensorDtype>(entry.Value.Dtype, ignoreCase: true),
                        Begin = entry.Value.Begin,
                        End = entry.Value.End
                    }, StringComparer.Ordinal);
                    if (companion.Role == "text-model") { SupraCheckpointSchema.ValidateText(tensors); }
                    else { SupraCheckpointSchema.ValidateVae(tensors); }
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
