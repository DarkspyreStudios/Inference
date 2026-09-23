using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.STT;

/// <summary>Interleaved floating-point PCM. The caller retains ownership and must not mutate it during a request.</summary>
public sealed record SpeechAudio(ReadOnlyMemory<float> Samples, int SampleRate, int Channels);

public sealed record SpeechToTextOptions(string? Language = null, bool TranslateToEnglish = false);

public sealed record SpeechTranscriptionRequest(SpeechAudio Audio, SpeechToTextOptions? Options = null, string? ModelId = null);

/// <summary>Timestamps are relative to the beginning of the supplied clip or window.</summary>
public sealed record SpeechSegment(string Text, TimeSpan Start, TimeSpan End);

public sealed record SpeechTranscriptionResult(string Text, IReadOnlyList<SpeechSegment> Segments,
    string? DetectedLanguage, InferenceProvenance Provenance);

public sealed record SpeechAudioWindow(string Id, SpeechAudio Audio);

/// <summary>A completed window, not a partial-token event. Timestamps in Transcription remain window-relative.</summary>
public sealed record SpeechWindowTranscription(string WindowId, SpeechTranscriptionResult Transcription);

public sealed record SpeechToTextCapabilities(bool? EnglishOnly, bool? SupportsTranslationToEnglish,
    bool? SupportsLanguageDetection, IReadOnlyList<string>? Languages,
    int MinimumSampleRate, int MaximumSampleRate, int MaximumChannels, bool SupportsWindowStreaming = true);

public sealed record SpeechModelInfo(TensorModelKey Key, string DisplayName, long DownloadBytes,
    bool? EnglishOnly, bool? SupportsTranslationToEnglish);

/// <summary>The interchangeable speech-transcription contract. Load is explicit; requests never download or load weights.</summary>
public interface ITensorSpeechToTextInference : ITensorModelInstance
{
    InferenceImplementationDescriptor Descriptor { get; }
    Task<SpeechToTextCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SpeechModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<SpeechTranscriptionResult> TranscribeAsync(SpeechTranscriptionRequest request, CancellationToken cancellationToken = default);
    /// <summary>Holds a request lease until enumeration completes or is disposed. The host must dispose abandoned enumerators.</summary>
    IAsyncEnumerable<SpeechWindowTranscription> TranscribeStreamAsync(IAsyncEnumerable<SpeechAudioWindow> windows,
        SpeechToTextOptions options, CancellationToken cancellationToken = default);
}
