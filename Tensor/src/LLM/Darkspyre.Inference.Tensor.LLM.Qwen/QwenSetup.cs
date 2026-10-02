using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

public static class QwenSetup
{
    public static InferenceSetupDefinition Definition { get; } = new(
        new("darkspyre.tensor.qwen", "Darkspyre Tensor Qwen", InferenceImplementationKind.Tensor, InferenceTask.TextGeneration),
        "In-process Qwen text inference with explicitly installed weights and a host-owned resident model.");

    /// <summary>Creates the settings manager for Qwen packs over the host's pack manager and optional live instance.</summary>
    public static TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target,
        ITensorModelPackManager packs, ITensorLlmInference? instance = null,
        IReadOnlyList<TensorModelPackDefinition>? offeredModels = null, ITensorModelSource? source = null, ITensorArtifactReader? reader = null,
        ITensorBackendProbe? backendProbe = null, QwenRuntimeOwner? runtimeOwner = null)
    {
        IReadOnlyList<TensorModelPackDefinition> models = offeredModels ?? [TensorLlmModels.Qwen3FourBillionQ4Km];
        foreach (TensorModelPackDefinition model in models) { QwenModelCompatibility.Validate(model); }
        QwenRuntimeOwner owner = runtimeOwner ?? QwenRuntimeOwner.GetOrCreate();
        return new(Definition, target, models, packs, instance, backendProbe ?? new QwenBackendProbe(owner), new QwenModelResolver(source, reader, owner));
    }
}
