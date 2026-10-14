using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Persist;

namespace Darkspyre.Inference.Tensor.STT.Whisper;

/// <summary>
/// Registers Whisper speech transcription. Its packs live under the <c>Whisper</c> store key. The twelve
/// published variants are its built-in packs.
/// </summary>
public sealed class WhisperEngineProvider : ITensorEngineProvider
{
    /// <inheritdoc />
    public TensorEngineDefinition Definition { get; } = new(WhisperSetup.Definition, "Whisper");

    /// <inheritdoc />
    public IReadOnlyList<TensorModelPackDefinition> BuiltInModels { get; } = TensorSpeechModels.Whisper;

    /// <inheritdoc />
    public Task<IReadOnlyList<TensorModelPackDefinition>> GetInstalledModelsAsync(IPersist modelStore,
        CancellationToken cancellationToken = default) =>
        TensorInstalledPacks.ReportAsync(modelStore, Definition.ModelStoreKey, BuiltInModels, Accepts, new WhisperModelResolver(), cancellationToken);

    private static bool Accepts(TensorModelPackDefinition model)
    {
        if (model.Capability != TensorCapability.SpeechTranscription || model.RuntimeArchitecture != TensorSpeechModels.WhisperRuntimeArchitecture) { return false; }
        WhisperCompatibility.Validate(model);
        return true;
    }

    /// <inheritdoc />
    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend) =>
        new WhisperSpeechToTextInference(packs, backend);

    /// <inheritdoc />
    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        ITensorModelList models, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null, ITensorBackendProbe? backendProbe = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ITensorSpeechToTextInference? live = instance switch
        {
            null => null,
            ITensorSpeechToTextInference speech when speech.Descriptor.Id == Definition.Setup.Implementation.Id => speech,
            _ => throw new ArgumentException("The instance does not belong to the Whisper implementation.", nameof(instance))
        };
        return WhisperSetup.CreateSettingsManager(target, packs, models, live, source, reader, backendProbe);
    }
}
