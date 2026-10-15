using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Persist;
using Microsoft.ML.OnnxRuntime;

namespace Darkspyre.Inference.Tensor.Embedding.Gemma;

/// <summary>Setup metadata and settings for resident EmbeddingGemma text and image embeddings.</summary>
public static class GemmaEmbeddingSetup
{
    /// <summary>The identity and user-facing description of this engine.</summary>
    public static InferenceSetupDefinition Definition { get; } = new(
        new("darkspyre.tensor.embedding.gemma", "EmbeddingGemma", InferenceImplementationKind.Tensor, InferenceTask.Embedding),
        "On-device text and image embeddings through ONNX Runtime. Install a model and load it explicitly on CPU or WebGPU.")
    {
        Icon = SetupIcons.FromResource(typeof(GemmaEmbeddingSetup).Assembly, "Icons/gemma.svg"),
        Group = SetupProviderGroup.ThisComputer
    };

    /// <summary>Creates settings over the caller's pack manager and optional resident instance.</summary>
    public static TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target,
        ITensorModelPackManager packs, ITensorModelList models, ITensorEmbeddingInference? instance = null,
        ITensorBackendProbe? backendProbe = null)
    {
        return new(Definition, target, models, packs, instance, backendProbe ?? new GemmaBackendProbe());
    }
}

/// <summary>Supplies Gemma packs and unloaded instances through the shared Tensor engine contract.</summary>
public sealed class GemmaEmbeddingEngineProvider : ITensorEngineProvider
{
    /// <inheritdoc />
    public TensorEngineDefinition Definition { get; } = new(GemmaEmbeddingSetup.Definition, "EmbeddingGemma");

    /// <inheritdoc />
    public IReadOnlyList<TensorModelPackDefinition> BuiltInModels { get; } = [GemmaEmbeddingModels.EmbeddingGemma2];

    /// <inheritdoc />
    public Task<IReadOnlyList<TensorModelPackDefinition>> GetInstalledModelsAsync(IPersist modelStore,
        CancellationToken cancellationToken = default) =>
        TensorInstalledPacks.ReportAsync(modelStore, Definition.ModelStoreKey, BuiltInModels, Accepts, null, cancellationToken);

    private static bool Accepts(TensorModelPackDefinition model)
    {
        if (model.Capability != TensorCapability.Embedding || model.RuntimeArchitecture != GemmaEmbeddingModels.Architecture) { return false; }
        GemmaEmbeddingModels.Validate(model);
        return true;
    }

    /// <inheritdoc />
    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend) =>
        new GemmaEmbeddingInference(packs, backend);

    /// <inheritdoc />
    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        ITensorModelList models, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null, ITensorBackendProbe? backendProbe = null)
    {
        ITensorEmbeddingInference? live = instance switch
        {
            null => null,
            ITensorEmbeddingInference embedding when embedding.Descriptor.Id == Definition.Setup.Implementation.Id => embedding,
            _ => throw new ArgumentException("The instance does not belong to EmbeddingGemma.", nameof(instance))
        };
        return GemmaEmbeddingSetup.CreateSettingsManager(target, packs, models, live, backendProbe);
    }
}

internal sealed class GemmaBackendProbe : ITensorBackendProbe
{
    public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => Check(request, cancellationToken), cancellationToken);

    private static TensorBackendAvailability Check(TensorBackendProbeRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (TensorBackendProbes.ValidateLocalRequest(request, GemmaEmbeddingSetup.Definition.Implementation.Id) is { } invalid) return invalid;
        if (TensorBackendProbes.RejectUnboundNativePlan(request) is { } unbound) return unbound;
        TensorBackendKind backend = GemmaOnnxBackend.Resolve(request.RequestedBackend);
        if (!GemmaEmbeddingModels.Backends.Contains(backend))
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported, "EmbeddingGemma supports CPU and WebGPU.");
        try
        {
            using SessionOptions options = GemmaOnnxBackend.CreateOptions(backend);
            token.ThrowIfCancellationRequested();
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Available,
                $"{backend} runtime prerequisites are available. Model allocation is not guaranteed.");
        }
        catch (Exception error) when (error is InferenceException or OnnxRuntimeException or DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException or PlatformNotSupportedException)
        {
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unavailable,
                $"{backend} runtime/device prerequisites are unavailable.");
        }
    }
}
