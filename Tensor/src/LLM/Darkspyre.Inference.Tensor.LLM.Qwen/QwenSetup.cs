using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

public static class QwenSetup
{
    public static InferenceSetupDefinition Definition { get; } = new(
        new("darkspyre.tensor.qwen", "Darkspyre Tensor Qwen", InferenceImplementationKind.Tensor, InferenceTask.TextGeneration),
        "In-process Qwen text inference with explicitly installed weights and a host-owned resident model.");

    public static TensorSetupSession CreateSession(SetupConfigurationIdentity identity,
        ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        TensorSetupConfiguration? existing = null, ITensorLlmInference? instance = null,
        IReadOnlyList<TensorModelPackDefinition>? offeredModels = null, ITensorModelSource? source = null, ITensorArtifactReader? reader = null)
    {
        IReadOnlyList<TensorModelPackDefinition> models = offeredModels ?? [TensorLlmModels.Qwen3FourBillionQ4Km];
        foreach (TensorModelPackDefinition model in models) { QwenModelCompatibility.Validate(model); }
        return new(Definition, identity, target, models, packs, existing, instance, new QwenBackendProbe(),
            new QwenModelResolver(source, reader));
    }
}
