using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Image.Supra;

/// <summary>Registers Supra image generation. Its packs live under the <c>Supra</c> store key.</summary>
public sealed class SupraEngineProvider : ITensorEngineProvider
{
    /// <summary>The Supra setup definition, with its packs under the <c>Supra</c> store key.</summary>
    public TensorEngineDefinition Definition { get; } = new(SupraSetup.Definition, "Supra");

    /// <inheritdoc />
    public IReadOnlyList<TensorModelPackDefinition> BuiltInModels { get; } = [TensorImageModels.Supra2Image];

    /// <inheritdoc />
    public Task<TensorInstalledReport> GetInstalledModelsAsync(TensorModelPackManager packs,
        CancellationToken cancellationToken = default) =>
        TensorInstalledPacks.ReportAsync(packs, BuiltInModels, Accepts, new SupraModelResolver(), cancellationToken);

    private static bool Accepts(TensorModelPackDefinition model)
    {
        if (model.Capability != TensorCapability.Image || model.RuntimeArchitecture != TensorImageModels.SupraRuntimeArchitecture) { return false; }
        SupraModelCompatibility.Validate(model);
        return true;
    }

    /// <inheritdoc />
    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend, string scratchDirectory) =>
        new SupraImageInference(packs, backend);

    /// <inheritdoc />
    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        ITensorModelList models, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null, ITensorBackendProbe? backendProbe = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ITensorImageInference? live = instance switch
        {
            null => null,
            ITensorImageInference image when image.Descriptor.Id == Definition.Setup.Implementation.Id => image,
            _ => throw new ArgumentException("The instance does not belong to the Supra implementation.", nameof(instance))
        };
        return SupraSetup.CreateSettingsManager(target, packs, models, live, source, reader, backendProbe);
    }
}
