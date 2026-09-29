using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

/// <summary>
/// Registers Laya decisions. Its packs live under the <c>Laya</c> store key. Laya has no built-in pack:
/// it offers only the compatible packs registered in the host's pack manager, and an empty list still
/// permits setup and import of a supported checkpoint.
/// </summary>
public sealed class LayaEngineProvider : ITensorEngineProvider
{
    public TensorEngineDefinition Definition { get; } = new(LayaSetup.Definition, "Laya");

    public async Task<IReadOnlyList<TensorModelPackDefinition>> GetOfferedModelsAsync(ITensorModelPackManager packs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packs);
        IReadOnlyList<TensorModelPackDefinition> registered = await packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TensorModelPackDefinition> offered = new TensorModelCatalog(registered.Where(model =>
            model.Capability == TensorCapability.Decision && model.RuntimeArchitecture == TensorDecisionModels.LayaRuntimeArchitecture)).Definitions;
        foreach (TensorModelPackDefinition model in offered) { LayaModelCompatibility.Validate(model); }
        return offered;
    }

    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend) =>
        new LayaDecisionInference(packs, backend);

    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        IReadOnlyList<TensorModelPackDefinition> offeredModels, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null)
    {
        ITensorDecisionInference? live = instance switch
        {
            null => null,
            ITensorDecisionInference decision when decision.Descriptor.Id == Definition.Setup.Implementation.Id => decision,
            _ => throw new ArgumentException("The instance does not belong to the Laya implementation.", nameof(instance))
        };
        return LayaSetup.CreateSettingsManager(target, packs, offeredModels, live, source, reader);
    }
}
