using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

/// <summary>Setup metadata and settings for the in-process Qwen text engine.</summary>
public static class QwenSetup
{
    /// <summary>The Qwen engine's implementation identity and description in the inference catalog.</summary>
    public static InferenceSetupDefinition Definition { get; } = new(
        new("darkspyre.tensor.qwen", "Darkspyre Tensor Qwen", InferenceImplementationKind.Tensor, InferenceTask.TextGeneration),
        "In-process Qwen text inference with explicitly installed weights and a host-owned resident model.");

    /// <summary>Creates the settings manager for Qwen packs over the host's pack manager and optional live instance.</summary>
    public static TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target,
        ITensorModelPackManager packs, ITensorModelList models, ITensorLlmInference? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null,
        ITensorBackendProbe? backendProbe = null, GgmlRuntimeOwner? runtimeOwner = null)
    {
        GgmlRuntimeOwner owner = runtimeOwner ?? GgmlRuntimeOwner.GetOrCreate();
        return new(Definition, target, models, packs, instance, backendProbe ?? new QwenBackendProbe(owner), new QwenModelResolver(source, reader, owner));
    }
}
