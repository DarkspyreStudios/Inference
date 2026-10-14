using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Persist;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

/// <summary>
/// Registers Laya decisions. Its packs live under the <c>Laya</c> store key. Laya has no built-in pack:
/// it reports only the compatible packs installed in the model store, and an empty list still
/// permits setup and import of a supported checkpoint.
/// </summary>
public sealed class LayaEngineProvider : ITensorEngineProvider
{
    /// <inheritdoc />
    public TensorEngineDefinition Definition { get; } = new(LayaSetup.Definition, "Laya");

    /// <inheritdoc />
    public IReadOnlyList<TensorModelPackDefinition> BuiltInModels { get; } = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<TensorModelPackDefinition>> GetInstalledModelsAsync(IPersist modelStore,
        CancellationToken cancellationToken = default) =>
        TensorInstalledPacks.ReportAsync(modelStore, Definition.ModelStoreKey, BuiltInModels, Accepts, new LayaModelResolver(), cancellationToken);

    private static bool Accepts(TensorModelPackDefinition model)
    {
        if (model.Capability != TensorCapability.Decision || model.RuntimeArchitecture != TensorDecisionModels.LayaRuntimeArchitecture) { return false; }
        LayaModelCompatibility.Validate(model);
        return true;
    }

    /// <inheritdoc />
    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend) =>
        new LayaDecisionInference(packs, backend);

    /// <inheritdoc />
    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        ITensorModelList models, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null, ITensorBackendProbe? backendProbe = null)
    {
        ITensorDecisionInference? live = instance switch
        {
            null => null,
            ITensorDecisionInference decision when decision.Descriptor.Id == Definition.Setup.Implementation.Id => decision,
            _ => throw new ArgumentException("The instance does not belong to the Laya implementation.", nameof(instance))
        };
        return LayaSetup.CreateSettingsManager(target, packs, models, live, source, reader, backendProbe);
    }
}
