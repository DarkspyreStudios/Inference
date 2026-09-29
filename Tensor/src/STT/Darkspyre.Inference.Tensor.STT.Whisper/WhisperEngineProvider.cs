using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.STT.Whisper;

/// <summary>
/// Registers Whisper speech transcription. Its packs live under the <c>Whisper</c> store key. The twelve
/// published variants are its built-in packs.
/// </summary>
public sealed class WhisperEngineProvider : ITensorEngineProvider
{
    public TensorEngineDefinition Definition { get; } = new(WhisperSetup.Definition, "Whisper");

    public async Task<IReadOnlyList<TensorModelPackDefinition>> GetOfferedModelsAsync(ITensorModelPackManager packs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packs);
        IReadOnlyList<TensorModelPackDefinition> registered = await packs.GetDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TensorModelPackDefinition> offered = new TensorModelCatalog(TensorSpeechModels.Whisper
            .Concat(registered.Where(model => model.Capability == TensorCapability.SpeechTranscription
                && model.RuntimeArchitecture == TensorSpeechModels.WhisperRuntimeArchitecture))).Definitions;
        foreach (TensorModelPackDefinition model in offered) { WhisperCompatibility.Validate(model); }
        return offered;
    }

    public ITensorModelInstance CreateInstance(ITensorModelPackManager packs, TensorBackendKind backend) =>
        new WhisperSpeechToTextInference(packs, backend);

    public TensorSettingsManager CreateSettingsManager(ISetupCommitTarget<TensorSetupChanges> target, ITensorModelPackManager packs,
        IReadOnlyList<TensorModelPackDefinition> offeredModels, ITensorModelInstance? instance = null,
        ITensorModelSource? source = null, ITensorArtifactReader? reader = null)
    {
        ArgumentNullException.ThrowIfNull(offeredModels);
        ITensorSpeechToTextInference? live = instance switch
        {
            null => null,
            ITensorSpeechToTextInference speech when speech.Descriptor.Id == Definition.Setup.Implementation.Id => speech,
            _ => throw new ArgumentException("The instance does not belong to the Whisper implementation.", nameof(instance))
        };
        return WhisperSetup.CreateSettingsManager(target, packs, live, offeredModels, source, reader);
    }
}
