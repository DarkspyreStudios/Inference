using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

/// <summary>Registers Qwen text generation. Its packs live under the <c>Qwen</c> store key.</summary>
public sealed class QwenEngineProvider(GgmlRuntimeOwner? runtimeOwner = null) : ITensorEngineProvider
{
    private readonly GgmlRuntimeOwner _runtimeOwner = runtimeOwner ?? GgmlRuntimeOwner.GetOrCreate();
    /// <inheritdoc />
    public TensorEngineDefinition Definition { get; } = new(QwenSetup.Definition, "Qwen");

    /// <inheritdoc />
    public async Task<IReadOnlyList<TensorModelPackDefinition>> GetOfferedModelsAsync(ITensorModelPackManager packs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packs);
        IReadOnlyList<TensorModelPackDefinition> registered = await packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TensorModelPackDefinition> offered = new TensorModelCatalog(new[] { TensorLlmModels.Qwen3FourBillionQ4Km }
            .Concat(registered.Where(model => model.Capability == TensorCapability.Llm
                && model.RuntimeArchitecture == TensorLlmModels.QwenRuntimeArchitecture))).Definitions;
        foreach (TensorModelPackDefinition model in offered) { QwenModelCompatibility.Validate(model); }
        return offered;
    }

    /// <inheritdoc />
    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend) =>
        new QwenLlmInference(packs, backend, runtimeOwner: _runtimeOwner);

    /// <inheritdoc />
    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        IReadOnlyList<TensorModelPackDefinition> offeredModels, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null, ITensorBackendProbe? backendProbe = null)
    {
        ArgumentNullException.ThrowIfNull(offeredModels);
        ITensorLlmInference? live = instance switch
        {
            null => null,
            ITensorLlmInference llm when llm.Descriptor.Id == Definition.Setup.Implementation.Id => llm,
            _ => throw new ArgumentException("The instance does not belong to the Qwen implementation.", nameof(instance))
        };
        return QwenSetup.CreateSettingsManager(target, packs, live, offeredModels, source, reader, backendProbe, _runtimeOwner);
    }
}
