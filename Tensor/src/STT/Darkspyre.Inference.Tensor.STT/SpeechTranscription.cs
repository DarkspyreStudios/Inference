using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.STT;

/// <summary>Interleaved floating-point PCM. The caller retains ownership and must not mutate it during a request.</summary>
public sealed record SpeechAudio(ReadOnlyMemory<float> Samples, int SampleRate, int Channels);

/// <summary>Options for a local speech transcription.</summary>
/// <param name="Language">The spoken language, or null to detect it.</param>
/// <param name="TranslateToEnglish">Whether to translate the speech into English text.</param>
public sealed record SpeechToTextOptions(string? Language = null, bool TranslateToEnglish = false);

/// <summary>A request to transcribe one audio clip.</summary>
/// <param name="Audio">The audio to transcribe.</param>
/// <param name="Options">The transcription options, or null for the defaults.</param>
/// <param name="ModelId">The model to use, or null for the loaded model.</param>
public sealed record SpeechTranscriptionRequest(SpeechAudio Audio, SpeechToTextOptions? Options = null, string? ModelId = null);

/// <summary>Timestamps are relative to the beginning of the supplied clip or window.</summary>
public sealed record SpeechSegment(string Text, TimeSpan Start, TimeSpan End);

/// <summary>The result of transcribing one audio clip or window.</summary>
/// <param name="Text">The full transcript.</param>
/// <param name="Segments">The transcript segments with timing.</param>
/// <param name="DetectedLanguage">The detected language, when reported.</param>
/// <param name="Provenance">Which implementation and model produced the transcript.</param>
public sealed record SpeechTranscriptionResult(string Text, IReadOnlyList<SpeechSegment> Segments,
    string? DetectedLanguage, InferenceProvenance Provenance);

/// <summary>One window of audio in a streaming transcription.</summary>
/// <param name="Id">The caller-chosen window identifier.</param>
/// <param name="Audio">The window audio.</param>
public sealed record SpeechAudioWindow(string Id, SpeechAudio Audio);

/// <summary>A completed window, not a partial-token event. Timestamps in Transcription remain window-relative.</summary>
public sealed record SpeechWindowTranscription(string WindowId, SpeechTranscriptionResult Transcription);

/// <summary>The speech-to-text features a model offers.</summary>
/// <param name="EnglishOnly">Whether the model only understands English, when known.</param>
/// <param name="SupportsTranslationToEnglish">Whether the model can translate speech into English, when known.</param>
/// <param name="SupportsLanguageDetection">Whether the model can detect the spoken language, when known.</param>
/// <param name="Languages">The languages the model accepts, when known.</param>
/// <param name="MinimumSampleRate">The lowest accepted sample rate, in hertz.</param>
/// <param name="MaximumSampleRate">The highest accepted sample rate, in hertz.</param>
/// <param name="MaximumChannels">The most audio channels accepted.</param>
/// <param name="SupportsWindowStreaming">Whether windowed streaming transcription is offered.</param>
public sealed record SpeechToTextCapabilities(bool? EnglishOnly, InferenceSupport? SupportsTranslationToEnglish,
    InferenceSupport? SupportsLanguageDetection, IReadOnlyList<string>? Languages,
    int MinimumSampleRate, int MaximumSampleRate, int MaximumChannels, InferenceSupport SupportsWindowStreaming = InferenceSupport.Supported);

/// <summary>A speech model the implementation offers.</summary>
/// <param name="Key">The model pack key.</param>
/// <param name="DisplayName">The name shown to users.</param>
/// <param name="DownloadBytes">The download size, in bytes.</param>
/// <param name="EnglishOnly">Whether the model only understands English, when known.</param>
/// <param name="SupportsTranslationToEnglish">Whether the model can translate speech into English, when known.</param>
public sealed record SpeechModelInfo(TensorModelKey Key, string DisplayName, long DownloadBytes,
    bool? EnglishOnly, InferenceSupport? SupportsTranslationToEnglish);

/// <summary>The interchangeable speech-transcription contract. Load is explicit; requests never download or load weights.</summary>
public interface ITensorSpeechToTextInference : ITensorModelInstance
{
    /// <summary>Describes the implementation and the tasks it performs.</summary>
    InferenceImplementationDescriptor Descriptor { get; }
    /// <summary>Returns the features offered by a model, or by the loaded model when null.</summary>
    Task<SpeechToTextCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default);
    /// <summary>Returns the speech models the implementation offers.</summary>
    Task<IReadOnlyList<SpeechModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);
    /// <summary>Transcribes one audio clip with the loaded model.</summary>
    Task<SpeechTranscriptionResult> TranscribeAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken = default);
    /// <summary>Holds a request lease until enumeration completes or is disposed. The host must dispose abandoned enumerators.</summary>
    IAsyncEnumerable<SpeechWindowTranscription> TranscribeStreamAsync(IAsyncEnumerable<SpeechAudioWindow> windows,
        SpeechToTextOptions options, CancellationToken cancellationToken = default);
}
