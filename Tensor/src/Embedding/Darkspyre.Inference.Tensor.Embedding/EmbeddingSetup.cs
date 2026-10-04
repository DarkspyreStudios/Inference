using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Embedding;

public static class EmbeddingSetup
{
    public static InferenceSetupDefinition Definition { get; } = new(
        new("darkspyre.tensor.embedding", "Darkspyre Tensor Embedding", InferenceImplementationKind.Tensor, InferenceTask.Embedding),
        "Resident text embeddings on this machine. Select and explicitly install a model; the host controls Load and Unload.");

    /// <summary>Creates the settings manager for embedding packs over the host's pack manager and optional live instance.</summary>
    public static TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target,
        ITensorModelPackManager packs, ITensorEmbeddingInference? instance = null,
        IReadOnlyList<TensorModelPackDefinition>? offeredModels = null, ITensorModelSource? source = null, ITensorArtifactReader? reader = null,
        ITensorBackendProbe? backendProbe = null)
    {
        IReadOnlyList<TensorModelPackDefinition> models = offeredModels ?? TensorEmbeddingModels.BuiltIn;
        foreach (TensorModelPackDefinition model in models) EmbeddingCompatibility.Validate(model);
        return new(Definition, target, models, packs, instance, backendProbe ?? new EmbeddingBackendProbe(), new EmbeddingModelResolver(source, reader));
    }
}

/// <summary>
/// Resolves embedding content by its pinned size and SHA-256. Other content stays Unverified: a filename or a GGUF
/// architecture string is not evidence that the encoder's pooling and tokenizer match a supported architecture.
/// </summary>
public sealed class EmbeddingModelResolver : TensorModelResolver
{
    public EmbeddingModelResolver(ITensorModelSource? source = null, ITensorArtifactReader? reader = null) : base(source, reader) { }
    public override IReadOnlyList<TensorArtifactRequirement> Requirements => [new("model", "*.gguf")];

    public override Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend,
        ITensorArtifactReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate); cancellationToken.ThrowIfCancellationRequested();
        static Task<TensorModelResolution> Failure(TensorCompatibilityState state, string code, string message) =>
            Task.FromResult(new TensorModelResolution(new(state, [new(code, message)], [])));
        if (backend is not (TensorBackendKind.Auto or TensorBackendKind.Cpu))
            return Failure(TensorCompatibilityState.Unsupported, "backend-unsupported", "Embeddings run on the managed CPU backend only.");
        TensorSourceArtifact[] selected = candidate.Artifacts.Where(item => item.Path == candidate.Selection.Variant).ToArray();
        if (selected.Length != 1)
            return Failure(TensorCompatibilityState.Unverified, "candidate-unavailable", "The selected source artifact is missing or ambiguous.");
        TensorSourceArtifact artifact = selected[0];
        TensorModelPackDefinition? known = TensorEmbeddingModels.KnownContent(artifact.Length, artifact.Sha256);
        if (known is null || string.IsNullOrWhiteSpace(candidate.License) || string.IsNullOrWhiteSpace(artifact.Provenance.License))
            return Failure(TensorCompatibilityState.Unverified, "embedding-content-unverified",
                "Resolution verifies the published embedding weights by their pinned size and SHA-256; other content requires further compatibility evidence.");
        TensorModelPackDefinition definition = TensorResolvedPack.Create(candidate, TensorCapability.Embedding, known.RuntimeArchitecture,
            known.SupportedBackends, [TensorResolvedPack.Artifact(artifact, "model", artifact.Path)], known);
        EmbeddingCompatibility.Validate(definition);
        return Task.FromResult(new TensorModelResolution(new(TensorCompatibilityState.Supported, [], [new(artifact.Path, known.RuntimeArchitecture,
            "Published content identity verified. Installation verifies all bytes; Load validates the GGUF.")]), definition));
    }
}

/// <summary>The managed CPU embedding runtime needs no native library, so the CPU backend is always available.</summary>
internal sealed class EmbeddingBackendProbe : ITensorBackendProbe
{
    public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TensorBackendProbes.ValidateLocalRequest(request, EmbeddingSetup.Definition.Implementation.Id) is { } invalid) return Task.FromResult(invalid);
        return Task.FromResult(request.RequestedBackend is TensorBackendKind.Cpu or TensorBackendKind.Auto
            ? TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Available, "The managed CPU embedding runtime needs no native library.")
            : TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported, "Embeddings run on the managed CPU backend only."));
    }
}

/// <summary>
/// Registers text embeddings. Its packs live under the <c>Embedding</c> store key. Arctic Embed L v2.0 is its
/// built-in pack.
/// </summary>
public sealed class EmbeddingEngineProvider : ITensorEngineProvider
{
    public TensorEngineDefinition Definition { get; } = new(EmbeddingSetup.Definition, "Embedding");

    public async Task<IReadOnlyList<TensorModelPackDefinition>> GetOfferedModelsAsync(ITensorModelPackManager packs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packs);
        IReadOnlyList<TensorModelPackDefinition> registered = await packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TensorModelPackDefinition> offered = new TensorModelCatalog(TensorEmbeddingModels.BuiltIn
            .Concat(registered.Where(model => model.Capability == TensorCapability.Embedding
                && TensorEmbeddingModels.ArchitectureOf(model) is not null))).Definitions;
        foreach (TensorModelPackDefinition model in offered) { EmbeddingCompatibility.Validate(model); }
        return offered;
    }

    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend) =>
        new TensorEmbeddingInference(packs, backend);

    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        IReadOnlyList<TensorModelPackDefinition> offeredModels, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null, ITensorBackendProbe? backendProbe = null)
    {
        ArgumentNullException.ThrowIfNull(offeredModels);
        ITensorEmbeddingInference? live = instance switch
        {
            null => null,
            ITensorEmbeddingInference embedding when embedding.Descriptor.Id == Definition.Setup.Implementation.Id => embedding,
            _ => throw new ArgumentException("The instance does not belong to the embedding implementation.", nameof(instance))
        };
        return EmbeddingSetup.CreateSettingsManager(target, packs, live, offeredModels, source, reader, backendProbe);
    }
}
