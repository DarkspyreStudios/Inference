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
    /// Creates the settings manager over the host's offered Laya packs. Register these same definitions in
    /// the catalog used by packs. Saving selects a key; installation and loading remain explicit actions.
    /// An empty list offers no models.
    /// </summary>
    public static TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target,
        ITensorModelPackManager packs, IReadOnlyList<TensorModelPackDefinition> offeredModels,
        ITensorDecisionInference? instance = null, ITensorModelSource? source = null, ITensorArtifactReader? reader = null,
        ITensorBackendProbe? backendProbe = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(offeredModels);
        foreach (TensorModelPackDefinition definition in offeredModels) { LayaModelCompatibility.Validate(definition); }
        return new(Definition, target, offeredModels, packs, instance, backendProbe ?? new LayaBackendProbe(), new LayaModelResolver(source, reader));
    }
}
