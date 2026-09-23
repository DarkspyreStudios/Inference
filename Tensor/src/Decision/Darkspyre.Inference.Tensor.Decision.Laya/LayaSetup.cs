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
    /// Passing no offered list uses the built-in pack. An empty list offers no models.
    /// </summary>
    public static TensorSetupSession CreateSession(SetupConfigurationIdentity identity,
        ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        IReadOnlyList<TensorModelPackDefinition>? offeredModels = null,
        TensorSetupConfiguration? existing = null, ITensorDecisionInference? instance = null)
    {
        ArgumentNullException.ThrowIfNull(packs);
        IReadOnlyList<TensorModelPackDefinition> models = offeredModels ?? [TensorDecisionModels.LayaTypedDecisions];
        foreach (TensorModelPackDefinition definition in models) { LayaModelCompatibility.Validate(definition); }
        return new(Definition, identity, target, models, packs, existing, instance);
    }
}
