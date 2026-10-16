using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.STT.Whisper;

internal sealed record WhisperModelMetadata(bool EnglishOnly, InferenceSupport SupportsTranslation, int LanguageCount)
{
    // Token IDs are ordered by the Whisper vocabulary, not by culture/display-language sorting.
    private static readonly string[] LanguageCodes = ("en zh de es ru ko fr ja pt tr pl ca nl ar sv it id hi fi vi he uk el ms cs ro da hu ta no th ur hr bg lt la mi ml cy sk te fa lv bn sr az sl kn et mk br eu is hy ne mn bs kk sq sw gl mr pa si km sn yo so af oc ka be tg sd gu am yi lo uz fo ht ps tk nn mt sa lb my bo tl mg as tt haw ln ha ba jw su yue").Split(' ');
    public IReadOnlyList<string> Languages => Array.AsReadOnly(EnglishOnly ? ["en"] : LanguageCodes.Take(LanguageCount).ToArray());
    public SpeechToTextCapabilities Capabilities => new(EnglishOnly, SupportsTranslation, EnglishOnly ? InferenceSupport.Unsupported : InferenceSupport.Supported, Languages,
        WhisperPcm.MinimumRate, WhisperPcm.MaximumRate, WhisperPcm.MaximumChannels);

    public string Language(SpeechToTextOptions options)
    {
        if (options.TranslateToEnglish && (SupportsTranslation == InferenceSupport.Unsupported))
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "This Whisper model does not support translation to English.");
        if (options.Language is null) return EnglishOnly ? "en" : "auto";
        string language = options.Language.ToLowerInvariant();
        if (!Languages.Contains(language, StringComparer.Ordinal))
            throw new InferenceException(InferenceFailureCode.InvalidRequest, "Select a supported Whisper language code, or null for automatic detection.");
        return language;
    }
}

internal static class WhisperCompatibility
{
    internal static void Validate(TensorModelPackDefinition definition)
    {
        TensorModelDefinitionValidator.Validate(definition);
        if (definition.Capability != TensorCapability.SpeechTranscription
            || definition.RuntimeArchitecture != TensorSpeechModels.WhisperRuntimeArchitecture
            || definition.Artifacts.Count != 1 || definition.Artifacts[0].Role != "model"
            || definition.Artifacts[0].Length < 48 || !definition.SupportedBackends.Contains(TensorBackendKind.Cpu)
            || definition.SupportedBackends.Any(backend => backend is not (TensorBackendKind.Cpu or TensorBackendKind.GgmlCuda)))
            throw new ArgumentException("Whisper requires one self-contained GGML model, CPU support and optional GGML CUDA support.", nameof(definition));
    }

    internal static WhisperModelMetadata Inspect(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x67676d6c) throw new InvalidDataException("Not a Whisper GGML checkpoint.");
        int vocabulary = reader.ReadInt32(), audioContext = reader.ReadInt32(), audioState = reader.ReadInt32();
        int audioHeads = reader.ReadInt32(), audioLayers = reader.ReadInt32(), textContext = reader.ReadInt32();
        int textState = reader.ReadInt32(), textHeads = reader.ReadInt32(), textLayers = reader.ReadInt32();
        int mels = reader.ReadInt32(), encoding = reader.ReadInt32();
        bool turbo = audioLayers == 32 && textLayers == 4;
        bool architecture = (audioState, audioLayers) is (384, 4) or (512, 6) or (768, 12) or (1024, 24) or (1280, 32);
        if (!architecture || audioContext != 1500 || textContext != 448 || textState != audioState
            || audioHeads * 64L != audioState || textHeads != audioHeads || (textLayers != audioLayers && !turbo)
            || vocabulary is not (51864 or 51865 or 51866) || mels != (vocabulary == 51866 ? 128 : 80)
            || (vocabulary == 51866 && audioLayers != 32) || (turbo && vocabulary != 51866)
            || encoding is not (0 or 1))
            throw new InvalidDataException("Unsupported Whisper architecture, vocabulary or weight encoding.");
        return new(vocabulary == 51864, vocabulary != 51864 && !turbo ? InferenceSupport.Supported : InferenceSupport.Unsupported, vocabulary == 51866 ? 100 : 99);
    }
}
