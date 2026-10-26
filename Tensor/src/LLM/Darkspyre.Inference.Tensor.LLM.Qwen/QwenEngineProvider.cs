using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

/// <summary>Registers Qwen text generation. Its packs live under the <c>Qwen</c> store key and arrive by import.</summary>
public sealed class QwenEngineProvider(GgmlRuntimeOwner? runtimeOwner = null) : ITensorEngineProvider
{
    private readonly GgmlRuntimeOwner _runtimeOwner = runtimeOwner ?? GgmlRuntimeOwner.GetOrCreate();
    /// <inheritdoc />
    public TensorEngineDefinition Definition { get; } = new(QwenSetup.Definition, "Qwen");

    /// <inheritdoc />
    public IReadOnlyList<TensorModelPackDefinition> BuiltInModels { get; } = [];

    /// <inheritdoc />
    public Task<TensorInstalledReport> GetInstalledModelsAsync(TensorModelPackManager packs,
        CancellationToken cancellationToken = default) =>
        TensorInstalledPacks.ReportAsync(packs, BuiltInModels, Accepts,
            new QwenModelResolver(runtimeOwner: _runtimeOwner), cancellationToken);

    private static bool Accepts(TensorModelPackDefinition model)
    {
        if (model.Capability != TensorCapability.Llm || model.RuntimeArchitecture != TensorLlmModels.QwenRuntimeArchitecture) { return false; }
        QwenModelCompatibility.Validate(model);
        return true;
    }

    /// <inheritdoc />
    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend, string scratchDirectory) =>
        new QwenLlmInference(packs, backend, scratchDirectory, runtimeOwner: _runtimeOwner);

    /// <inheritdoc />
    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        ITensorModelList models, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null, ITensorBackendProbe? backendProbe = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ITensorLlmInference? live = instance switch
        {
            null => null,
            ITensorLlmInference llm when llm.Descriptor.Id == Definition.Setup.Implementation.Id => llm,
            _ => throw new ArgumentException("The instance does not belong to the Qwen implementation.", nameof(instance))
        };
        return QwenSetup.CreateSettingsManager(target, packs, models, live, source, reader, backendProbe, _runtimeOwner);
    }
}
