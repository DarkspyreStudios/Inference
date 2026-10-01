using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Image.Supra;

public static class SupraSetup
{
    public static InferenceSetupDefinition Definition { get; } = new(
        new("darkspyre.tensor.supra2-img", "Darkspyre Tensor Supra2-IMG", InferenceImplementationKind.Tensor, InferenceTask.ImageGeneration),
        "In-process Supra2 image generation using a complete DiT, text encoder, tokenizer and VAE pack.");

    /// <summary>Creates the settings manager for Supra packs over the host's pack manager and optional live instance.</summary>
    public static TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target,
        ITensorModelPackManager packs, ITensorImageInference? instance = null,
        IReadOnlyList<TensorModelPackDefinition>? offeredModels = null, ITensorModelSource? source = null, ITensorArtifactReader? reader = null,
        ITensorBackendProbe? backendProbe = null)
    {
        IReadOnlyList<TensorModelPackDefinition> models = offeredModels ?? [TensorImageModels.Supra2Image];
        foreach (TensorModelPackDefinition model in models) { SupraModelCompatibility.Validate(model); }
        return new(Definition, target, models, packs, instance,
            backendProbe ?? TensorBackendProbes.CpuCuda(global::TensorSharp.Cuda.CudaBackend.IsAvailable),
            new SupraModelResolver(source, reader));
    }
}
