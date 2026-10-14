using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Image.Supra;

/// <summary>Setup entry points for the in-process Supra2 image generation engine.</summary>
public static class SupraSetup
{
    /// <summary>The implementation identity, task and description of the Supra2 image engine.</summary>
    public static InferenceSetupDefinition Definition { get; } = new(
        new("darkspyre.tensor.supra2-img", "Darkspyre Tensor Supra2-IMG", InferenceImplementationKind.Tensor, InferenceTask.ImageGeneration),
        "In-process Supra2 image generation using a complete DiT, text encoder, tokenizer and VAE pack.");

    /// <summary>Creates the settings manager for Supra packs over the host's pack manager and optional live instance.</summary>
    public static TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target,
        ITensorModelPackManager packs, ITensorModelList models, ITensorImageInference? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null,
        ITensorBackendProbe? backendProbe = null)
    {
        return new(Definition, target, models, packs, instance,
            backendProbe ?? TensorBackendProbes.CpuCuda(global::TensorSharp.Cuda.CudaBackend.IsAvailable),
            new SupraModelResolver(source, reader));
    }
}
