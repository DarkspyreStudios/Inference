using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

public static class LayaSetup
{
    public static InferenceSetupDefinition Definition { get; } = new(
        new("darkspyre.tensor.laya", "Darkspyre Tensor Laya", InferenceImplementationKind.Tensor, InferenceTask.Decision),
        "In-process typed decisions using a host-selected, compatible Laya checkpoint.");

    /// <summary>
    /// Creates an editor over the host's offered Laya packs. Register these same definitions in the
    /// catalog used by packs. Saving selects a key; installation and loading remain explicit actions.
    /// An empty list offers no models.
    /// </summary>
    public static TensorSetupSession CreateSession(SetupConfigurationIdentity identity,
        ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        IReadOnlyList<TensorModelPackDefinition> offeredModels,
        TensorSetupConfiguration? existing = null, ITensorDecisionInference? instance = null, ITensorModelSource? source = null,
        ITensorArtifactReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        ArgumentNullException.ThrowIfNull(offeredModels);
        foreach (TensorModelPackDefinition definition in offeredModels) { LayaModelCompatibility.Validate(definition); }
        return new(Definition, identity, target, offeredModels, packs, existing, instance, new LayaBackendProbe(), new LayaModelResolver(source, reader));
    }
}
