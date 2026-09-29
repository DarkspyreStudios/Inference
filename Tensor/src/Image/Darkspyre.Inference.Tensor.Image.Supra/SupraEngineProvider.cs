using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Image.Supra;

/// <summary>Registers Supra image generation. Its packs live under the <c>Supra</c> store key.</summary>
public sealed class SupraEngineProvider : ITensorEngineProvider
{
    public TensorEngineDefinition Definition { get; } = new(SupraSetup.Definition, "Supra");

    public async Task<IReadOnlyList<TensorModelPackDefinition>> GetOfferedModelsAsync(ITensorModelPackManager packs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packs);
        IReadOnlyList<TensorModelPackDefinition> registered = await packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TensorModelPackDefinition> offered = new TensorModelCatalog(new[] { TensorImageModels.Supra2Image }
            .Concat(registered.Where(model => model.Capability == TensorCapability.Image
                && model.RuntimeArchitecture == TensorImageModels.SupraRuntimeArchitecture))).Definitions;
        foreach (TensorModelPackDefinition model in offered) { SupraModelCompatibility.Validate(model); }
        return offered;
    }

    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend) =>
        new SupraImageInference(packs, backend);

    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        IReadOnlyList<TensorModelPackDefinition> offeredModels, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(offeredModels);
        ITensorImageInference? live = instance switch
        {
            null => null,
            ITensorImageInference image when image.Descriptor.Id == Definition.Setup.Implementation.Id => image,
            _ => throw new ArgumentException("The instance does not belong to the Supra implementation.", nameof(instance))
        };
        return SupraSetup.CreateSettingsManager(target, packs, live, offeredModels, source, reader);
    }
}
