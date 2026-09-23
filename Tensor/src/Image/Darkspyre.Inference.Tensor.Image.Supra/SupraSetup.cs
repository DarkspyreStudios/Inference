using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Image.Supra;

public static class SupraSetup
{
    public static InferenceSetupDefinition Definition { get; } = new(
        new("darkspyre.tensor.supra2-img", "Darkspyre Tensor Supra2-IMG", InferenceImplementationKind.Tensor, InferenceTask.ImageGeneration),
        "In-process Supra2 image generation using a complete DiT, text encoder, tokenizer and VAE pack.");

    public static TensorSetupSession CreateSession(SetupConfigurationIdentity identity,
        ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        TensorSetupConfiguration? existing = null, ITensorImageInference? instance = null) =>
        new(Definition, identity, target, [TensorImageModels.Supra2Image], packs, existing, instance);
}
