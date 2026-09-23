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
        TensorSetupConfiguration? existing = null, ITensorLlmInference? instance = null) =>
        new(Definition, identity, target, [TensorLlmModels.Qwen3FourBillionQ4Km], packs, existing, instance);
}
