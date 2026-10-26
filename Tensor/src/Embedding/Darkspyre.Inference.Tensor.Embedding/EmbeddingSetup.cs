using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.Embedding;

/// <summary>The setup definition and settings-manager factory for local text embeddings.</summary>
public static class EmbeddingSetup
{
    /// <summary>Identifies the Darkspyre Tensor Embedding implementation and describes it to the user.</summary>
    public static InferenceSetupDefinition Definition { get; } = new(
        new("darkspyre.tensor.embedding", "Darkspyre Tensor Embedding", InferenceImplementationKind.Tensor, InferenceTask.Embedding),
        "Resident text embeddings on this machine. Select and explicitly install a model; the host controls Load and Unload.");

    /// <summary>Creates the settings manager for embedding packs over the host's pack manager and optional live instance.</summary>
    public static TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target,
        ITensorModelPackManager packs, ITensorModelList models, ITensorEmbeddingInference? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null,
        ITensorBackendProbe? backendProbe = null)
    {
        return new(Definition, target, models, packs, instance, backendProbe ?? new EmbeddingBackendProbe(), new EmbeddingModelResolver(source, reader));
    }
}

/// <summary>
/// Resolves a GGUF embedding model from its header: architecture, pooling, dimensions, context and tokenizer must be ones
/// this engine runs.
/// </summary>
public sealed class EmbeddingModelResolver : TensorModelResolver
{
    /// <summary>Creates a resolver that discovers candidates through <paramref name="source"/> and reads their metadata through <paramref name="reader"/>.</summary>
    public EmbeddingModelResolver(ITensorModelSource? source = null, ITensorArtifactReader? reader = null) : base(source, reader) { }
    /// <inheritdoc />
    public override IReadOnlyList<TensorArtifactRequirement> Requirements => [new("model", "*.gguf")];

    /// <inheritdoc />
    public override async Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend,
        ITensorArtifactReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate); cancellationToken.ThrowIfCancellationRequested();
        var evidence = new List<TensorCompatibilityEvidence>();
        TensorModelResolution Failure(TensorCompatibilityState state, string code, string message, TensorSourceState sourceState = TensorSourceState.Available) =>
            new(new(state, [new(code, message)], evidence.ToArray(), sourceState));
        if (backend != TensorBackendKind.Auto && !TensorEmbeddingModels.Backends.Contains(backend))
            return Failure(TensorCompatibilityState.Unsupported, "backend-unsupported", "Embeddings run on managed CPU or a GGML backend.");
        TensorSourceArtifact[] selected = candidate.Artifacts.Where(item => item.Path == candidate.Selection.Variant).ToArray();
        if (selected.Length != 1)
            return Failure(TensorCompatibilityState.Unverified, "candidate-unavailable", "The selected source artifact is missing or ambiguous.");
        TensorSourceArtifact artifact = selected[0];
        if (artifact.Length is not > 0 || artifact.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(candidate.License) || string.IsNullOrWhiteSpace(artifact.Provenance.License))
            return Failure(TensorCompatibilityState.Unverified, "pack-metadata-incomplete", "Every artifact needs a pinned source, length, SHA-256 and license before resolution.");
        try
        {
            GgufHeader header = await new TensorMetadataReader(reader).ReadAsync(artifact, stream => GgufHeader.Read(stream), cancellationToken)
                .ConfigureAwait(false);
            evidence.Add(new(artifact.Path, "gguf-header", $"GGUF {header.Version}; {header.Tensors.Count} tensors."));
            // Qwen3-Embedding pools the last token; a Qwen3 chat model of the same GGUF architecture names no pooling.
            if (header.Values.GetValueOrDefault("general.architecture") is "qwen3"
                && header.Values.GetValueOrDefault("qwen3.pooling_type") is 3u)
                return Failure(TensorCompatibilityState.Unsupported, TensorInstalledPacks.RetiredArchitectureCode,
                    TensorEmbeddingModels.Qwen3EmbeddingRetiredReason);
            TensorEmbeddingArchitecture architecture = EmbeddingCompatibility.MatchHeader(header.Values);
            TensorModelPackDefinition definition = TensorResolvedPack.Create(candidate, TensorCapability.Embedding, architecture.Name,
                architecture.Backends, [TensorResolvedPack.Artifact(artifact, "model", artifact.Path)]);
            EmbeddingCompatibility.Validate(definition);
            evidence.Add(new(artifact.Path, architecture.Name,
                "Embedding header accepted: pooling, dimensions, context and tokenizer. Installation verifies all bytes; Load validates the tensors."));
            return new(new(TensorCompatibilityState.Supported, [], evidence.ToArray()), definition);
        }
        catch (TensorSourceException exception)
        { return Failure(TensorCompatibilityState.Unverified, exception.Code, exception.Message, exception.State); }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or ArgumentException or OverflowException)
        { return Failure(TensorCompatibilityState.Unsupported, "embedding-schema", exception.Message); }
        catch (IOException exception)
        { return Failure(TensorCompatibilityState.Unverified, "metadata-incomplete", $"The embedding GGUF header could not be inspected completely: {exception.Message}"); }
    }
}

/// <summary>
/// The managed CPU encoder needs no native library, so CPU is always available. Auto and the GGML backends are answered
/// by the shared GGML runtime owner, the same one the engine borrows.
/// </summary>
internal sealed class EmbeddingBackendProbe(GgmlRuntimeOwner? runtimeOwner = null) : ITensorBackendProbe
{
    public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TensorBackendProbes.ValidateLocalRequest(request, EmbeddingSetup.Definition.Implementation.Id) is { } invalid) return Task.FromResult(invalid);
        if (request.RequestedBackend == TensorBackendKind.Cpu)
            return Task.FromResult(TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Available,
                "The managed CPU embedding encoder needs no native library."));
        if (request.RequestedBackend != TensorBackendKind.Auto && !TensorEmbeddingModels.Backends.Contains(request.RequestedBackend))
            return Task.FromResult(TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported,
                "Embeddings run on managed CPU or a GGML backend."));
        return (runtimeOwner ?? GgmlRuntimeOwner.GetOrCreate()).CheckAsync(request, cancellationToken);
    }
}

/// <summary>
/// Registers text embeddings. Its packs live under the <c>Embedding</c> store key and arrive by import. Its engines borrow <paramref name="runtimeOwner"/>.
/// </summary>
public sealed class EmbeddingEngineProvider(GgmlRuntimeOwner runtimeOwner) : ITensorEngineProvider
{
    /// <inheritdoc />
    public TensorEngineDefinition Definition { get; } = new(EmbeddingSetup.Definition, "Embedding");

    /// <inheritdoc />
    public IReadOnlyList<TensorModelPackDefinition> BuiltInModels { get; } = [];

    /// <inheritdoc />
    public Task<TensorInstalledReport> GetInstalledModelsAsync(TensorModelPackManager packs,
        CancellationToken cancellationToken = default) =>
        TensorInstalledPacks.ReportAsync(packs, BuiltInModels, Accepts, new EmbeddingModelResolver(), cancellationToken);

    private static bool Accepts(TensorModelPackDefinition model)
    {
        if (model.Capability != TensorCapability.Embedding || TensorEmbeddingModels.ArchitectureOf(model) is null) { return false; }
        EmbeddingCompatibility.Validate(model);
        return true;
    }

    /// <inheritdoc />
    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend, string scratchDirectory) =>
        new TensorEmbeddingInference(packs, backend, runtimeOwner);

    /// <inheritdoc />
    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        ITensorModelList models, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null, ITensorBackendProbe? backendProbe = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ITensorEmbeddingInference? live = instance switch
        {
            null => null,
            ITensorEmbeddingInference embedding when embedding.Descriptor.Id == Definition.Setup.Implementation.Id => embedding,
            _ => throw new ArgumentException("The instance does not belong to the embedding implementation.", nameof(instance))
        };
        return EmbeddingSetup.CreateSettingsManager(target, packs, models, live, source, reader,
            backendProbe ?? new EmbeddingBackendProbe(runtimeOwner));
    }
}
