using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

/// <summary>Setup entry points for the Tensor Laya decision implementation.</summary>
public static class LayaSetup
{
    /// <summary>Setup identity and description of the in-process Tensor Laya decision implementation.</summary>
    public static InferenceSetupDefinition Definition { get; } = new(
        new("darkspyre.tensor.laya", "Darkspyre Tensor Laya", InferenceImplementationKind.Tensor, InferenceTask.Decision),
        "In-process typed decisions using a host-selected, compatible Laya checkpoint.");

    /// <summary>
    /// Creates the settings manager over the host's live list of Laya models in the model store. Saving selects a key;
    /// installation and loading remain explicit actions.
    /// </summary>
    public static TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target,
        ITensorModelPackManager packs, ITensorModelList models,
        ITensorDecisionInference? instance = null, ITensorModelSource? source = null, ITensorArtifactReader? reader = null,
        ITensorBackendProbe? backendProbe = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(models);
        return new(Definition, target, models, packs, instance, backendProbe ?? new LayaBackendProbe(), new LayaModelResolver(source, reader));
    }
}
