using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.STT;

/// <summary>The encoding of raw or streamed speech audio.</summary>
public enum SpeechAudioEncoding
{
    /// <summary>16-bit signed PCM, little-endian.</summary>
    Pcm16LittleEndian = 0,

    /// <summary>G.711 mu-law.</summary>
    Mulaw = 1,

    /// <summary>G.711 A-law.</summary>
    Alaw = 2,

    /// <summary>Opus audio.</summary>
    Opus = 3
}

/// <summary>Audio supplied for a single transcription request.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(BinarySpeechInput), "binary")]
[JsonDerivedType(typeof(UrlSpeechInput), "url")]
[JsonDerivedType(typeof(ResourceSpeechInput), "resource")]
public abstract record SpeechTranscriptionInput
{
    private protected SpeechTranscriptionInput() { }
}

/// <summary>Audio bytes supplied with the request. The bytes are copied on construction.</summary>
/// <param name="ContentType">The media type of the audio.</param>
/// <param name="Data">The audio bytes.</param>
/// <param name="FileName">The file name of the audio, when known.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BinarySpeechInput(string ContentType, ReadOnlyMemory<byte> Data,
    string? FileName = null) : SpeechTranscriptionInput
{
    /// <summary>The media type of the audio.</summary>
    [JsonRequired] public string ContentType { get; init => field = SpeechData.Required(value); } = SpeechData.Required(ContentType);
    /// <summary>A private copy of the audio bytes.</summary>
    [JsonRequired] public ReadOnlyMemory<byte> Data { get; init => field = value.ToArray(); } = Data.ToArray();
    /// <summary>Returns a placeholder that hides the audio content.</summary>
    public override string ToString() => "BinarySpeechInput { <data> }";
}

/// <summary>Audio at a URL that the provider fetches.</summary>
/// <param name="Url">The URL of the audio.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UrlSpeechInput(Uri Url) : SpeechTranscriptionInput
{
    /// <summary>The URL of the audio.</summary>
    [JsonRequired] public Uri Url { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Url);
    /// <summary>Returns a placeholder that hides the URL.</summary>
    public override string ToString() => "UrlSpeechInput { <data> }";
}

/// <summary>Audio stored as a workspace resource.</summary>
/// <param name="Resource">The workspace resource that holds the audio.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResourceSpeechInput(InferenceResourceReference Resource) : SpeechTranscriptionInput
{
    /// <summary>The workspace resource that holds the audio.</summary>
    [JsonRequired] public InferenceResourceReference Resource { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Resource);
    /// <summary>Returns a placeholder that hides the resource reference.</summary>
    public override string ToString() => "ResourceSpeechInput { <data> }";
}

/// <summary>Options for a single transcription request. Null values leave the provider default.</summary>
/// <param name="ModelId">The transcription model.</param>
/// <param name="Language">The spoken language.</param>
/// <param name="RawAudioFormat">The encoding of headerless audio.</param>
/// <param name="SampleRate">The sample rate of headerless audio, in hertz.</param>
/// <param name="Diarization">Whether to label words with speakers.</param>
/// <param name="Multichannel">Whether to transcribe each channel separately.</param>
/// <param name="Channels">The number of audio channels.</param>
/// <param name="KeyTerms">Terms the model is biased to recognize.</param>
/// <param name="IncludeFillerWords">Whether to keep filler words such as "um".</param>
/// <param name="VadThreshold">The voice activity detection threshold.</param>
/// <param name="TextNormalization">Whether to normalize numbers and other text in the transcript.</param>
public sealed record SpeechTranscriptionOptions(string? ModelId = null,
    string? Language = null, SpeechAudioEncoding? RawAudioFormat = null,
    int? SampleRate = null, bool? Diarization = null, bool? Multichannel = null,
    int? Channels = null, IReadOnlyList<string>? KeyTerms = null,
    bool? IncludeFillerWords = null, double? VadThreshold = null,
    bool? TextNormalization = null)
{
    /// <summary>The encoding of headerless audio. Throws when the value is not a defined encoding.</summary>
    public SpeechAudioEncoding? RawAudioFormat { get; init => field = SpeechData.Defined(value); } = SpeechData.Defined(RawAudioFormat);
    /// <summary>A private copy of the terms the model is biased to recognize.</summary>
    public IReadOnlyList<string>? KeyTerms { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(KeyTerms);
    /// <summary>The voice activity detection threshold. Throws when the value is not finite.</summary>
    public double? VadThreshold { get; init => field = SpeechData.Finite(value); } = SpeechData.Finite(VadThreshold);
    /// <summary>Returns a placeholder that hides the option values.</summary>
    public override string ToString() => "SpeechTranscriptionOptions { <data> }";
}

/// <summary>Options for a streaming transcription session. Null values leave the provider default.</summary>
/// <param name="ModelId">The transcription model.</param>
/// <param name="Language">The spoken language.</param>
/// <param name="Encoding">The encoding of the streamed audio.</param>
/// <param name="SampleRate">The sample rate of the streamed audio, in hertz.</param>
/// <param name="Diarization">Whether to label words with speakers.</param>
/// <param name="Multichannel">Whether to transcribe each channel separately.</param>
/// <param name="Channels">The number of audio channels.</param>
/// <param name="KeyTerms">Terms the model is biased to recognize.</param>
/// <param name="IncludeFillerWords">Whether to keep filler words such as "um".</param>
/// <param name="VadThreshold">The voice activity detection threshold.</param>
/// <param name="TextNormalization">Whether to normalize numbers and other text in the transcript.</param>
/// <param name="InterimResults">Whether to emit partial transcripts before they are final.</param>
/// <param name="Endpointing">The silence that ends an utterance.</param>
/// <param name="SmartTurnThreshold">The end-of-turn confidence, from 0 to 1, at which the provider ends a turn.</param>
/// <param name="SmartTurnTimeout">The longest wait for an end-of-turn decision.</param>
public sealed record SpeechStreamOptions(string? ModelId = null, string? Language = null,
    SpeechAudioEncoding? Encoding = null, int? SampleRate = null,
    bool? Diarization = null, bool? Multichannel = null, int? Channels = null,
    IReadOnlyList<string>? KeyTerms = null, bool? IncludeFillerWords = null,
    double? VadThreshold = null, bool? TextNormalization = null, bool? InterimResults = null,
    TimeSpan? Endpointing = null, double? SmartTurnThreshold = null,
    TimeSpan? SmartTurnTimeout = null)
{
    /// <summary>The encoding of the streamed audio. Throws when the value is not a defined encoding.</summary>
    public SpeechAudioEncoding? Encoding { get; init => field = SpeechData.Defined(value); } = SpeechData.Defined(Encoding);
    /// <summary>A private copy of the terms the model is biased to recognize.</summary>
    public IReadOnlyList<string>? KeyTerms { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(KeyTerms);
    /// <summary>The voice activity detection threshold. Throws when the value is not finite.</summary>
    public double? VadThreshold { get; init => field = SpeechData.Finite(value); } = SpeechData.Finite(VadThreshold);
    /// <summary>The end-of-turn confidence at which the provider ends a turn. Throws when the value is not finite.</summary>
    public double? SmartTurnThreshold { get; init => field = SpeechData.Finite(value); } = SpeechData.Finite(SmartTurnThreshold);
    /// <summary>Returns a placeholder that hides the option values.</summary>
    public override string ToString() => "SpeechStreamOptions { <data> }";
}

/// <summary>One transcribed word with its timing.</summary>
/// <param name="Text">The word text.</param>
/// <param name="Start">The offset where the word starts.</param>
/// <param name="End">The offset where the word ends.</param>
/// <param name="Confidence">The recognition confidence, when reported.</param>
/// <param name="Speaker">The speaker index from diarization, when reported.</param>
public sealed record SpeechWord(string Text, TimeSpan Start, TimeSpan End,
    double? Confidence = null, int? Speaker = null)
{
    /// <summary>The word text.</summary>
    [JsonRequired] public string Text { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Text);
    /// <summary>The offset where the word starts.</summary>
    [JsonRequired] public TimeSpan Start { get; init; } = Start;
    /// <summary>The offset where the word ends.</summary>
    [JsonRequired] public TimeSpan End { get; init; } = End;
    /// <summary>The recognition confidence, when reported. Throws when the value is not finite.</summary>
    public double? Confidence { get; init => field = SpeechData.Finite(value); } = SpeechData.Finite(Confidence);
    /// <summary>Returns a placeholder that hides the word.</summary>
    public override string ToString() => "SpeechWord { <data> }";
}

/// <summary>The transcript of one audio channel.</summary>
/// <param name="Index">The zero-based channel index.</param>
/// <param name="Text">The channel transcript.</param>
/// <param name="Language">The detected language, when reported.</param>
/// <param name="Words">The words with timing, when reported.</param>
public sealed record SpeechChannelTranscript(int Index, string Text,
    string? Language = null, IReadOnlyList<SpeechWord>? Words = null)
{
    /// <summary>The zero-based channel index.</summary>
    [JsonRequired] public int Index { get; init; } = Index;
    /// <summary>The channel transcript.</summary>
    [JsonRequired] public string Text { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Text);
    /// <summary>A private copy of the words with timing, when reported.</summary>
    public IReadOnlyList<SpeechWord>? Words { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(Words);
    /// <summary>Returns a placeholder that hides the transcript.</summary>
    public override string ToString() => "SpeechChannelTranscript { <data> }";
}

/// <summary>The result of a single transcription request.</summary>
/// <param name="Text">The full transcript.</param>
/// <param name="Provenance">Which implementation and model produced the transcript.</param>
/// <param name="Language">The detected language, when reported.</param>
/// <param name="Duration">The audio duration, when reported.</param>
/// <param name="Words">The words with timing, when reported.</param>
/// <param name="Channels">The per-channel transcripts, when multichannel transcription ran.</param>
public sealed record SpeechTranscriptionResult(string Text,
    InferenceProvenance Provenance, string? Language = null, TimeSpan? Duration = null,
    IReadOnlyList<SpeechWord>? Words = null,
    IReadOnlyList<SpeechChannelTranscript>? Channels = null)
{
    /// <summary>The full transcript.</summary>
    [JsonRequired] public string Text { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Text);
    /// <summary>Which implementation and model produced the transcript.</summary>
    [JsonRequired] public InferenceProvenance Provenance { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Provenance);
    /// <summary>A private copy of the words with timing, when reported.</summary>
    public IReadOnlyList<SpeechWord>? Words { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(Words);
    /// <summary>A private copy of the per-channel transcripts, when reported.</summary>
    public IReadOnlyList<SpeechChannelTranscript>? Channels { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(Channels);
    /// <summary>Returns a placeholder that hides the transcript.</summary>
    public override string ToString() => "SpeechTranscriptionResult { <data> }";
}

/// <summary>An event emitted by a streaming transcription session.</summary>
public abstract record SpeechTranscriptEvent
{
    private protected SpeechTranscriptEvent() { }
}

/// <summary>Reports that the provider opened the streaming session.</summary>
/// <param name="SessionId">The provider session identifier, when reported.</param>
public sealed record SpeechTranscriptCreatedEvent(string? SessionId = null) : SpeechTranscriptEvent
{
    /// <summary>Returns a placeholder that hides the event content.</summary>
    public override string ToString() => "SpeechTranscriptCreatedEvent { <data> }";
}

/// <summary>A transcript for part of the streamed audio.</summary>
/// <param name="Text">The transcript text.</param>
/// <param name="Start">The offset where the transcribed audio starts, when reported.</param>
/// <param name="Duration">The duration of the transcribed audio, when reported.</param>
/// <param name="IsFinal">Whether the text for this audio will not change again.</param>
/// <param name="SpeechFinal">Whether the provider detected the end of an utterance.</param>
/// <param name="ChannelIndex">The channel the text belongs to, when reported.</param>
/// <param name="Words">The words with timing, when reported.</param>
/// <param name="EndOfTurnConfidence">The provider's confidence that the speaker finished a turn, when reported.</param>
public sealed record SpeechTranscriptPartialEvent(string Text, TimeSpan? Start = null,
    TimeSpan? Duration = null, bool? IsFinal = null, bool? SpeechFinal = null,
    int? ChannelIndex = null, IReadOnlyList<SpeechWord>? Words = null,
    double? EndOfTurnConfidence = null) : SpeechTranscriptEvent
{
    /// <summary>The transcript text.</summary>
    [JsonRequired] public string Text { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Text);
    /// <summary>A private copy of the words with timing, when reported.</summary>
    public IReadOnlyList<SpeechWord>? Words { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(Words);
    /// <summary>The provider's confidence that the speaker finished a turn, when reported. Throws when the value is not finite.</summary>
    public double? EndOfTurnConfidence { get; init => field = SpeechData.Finite(value); } = SpeechData.Finite(EndOfTurnConfidence);
    /// <summary>Returns a placeholder that hides the event content.</summary>
    public override string ToString() => "SpeechTranscriptPartialEvent { <data> }";
}

/// <summary>The final transcript emitted when the stream completes.</summary>
/// <param name="Text">The transcript text.</param>
/// <param name="Duration">The duration of the transcribed audio, when reported.</param>
/// <param name="ChannelIndex">The channel the text belongs to, when reported.</param>
/// <param name="Words">The words with timing, when reported.</param>
public sealed record SpeechTranscriptDoneEvent(string Text, TimeSpan? Duration = null,
    int? ChannelIndex = null, IReadOnlyList<SpeechWord>? Words = null) : SpeechTranscriptEvent
{
    /// <summary>The transcript text.</summary>
    [JsonRequired] public string Text { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Text);
    /// <summary>A private copy of the words with timing, when reported.</summary>
    public IReadOnlyList<SpeechWord>? Words { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(Words);
    /// <summary>Returns a placeholder that hides the event content.</summary>
    public override string ToString() => "SpeechTranscriptDoneEvent { <data> }";
}

/// <summary>Reports a failure in the streaming session.</summary>
/// <param name="Error">The failure.</param>
public sealed record SpeechTranscriptErrorEvent(InferenceResourceFailure Error) : SpeechTranscriptEvent
{
    /// <summary>The failure.</summary>
    [JsonRequired] public InferenceResourceFailure Error { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Error);
    /// <summary>Returns a placeholder that hides the event content.</summary>
    public override string ToString() => "SpeechTranscriptErrorEvent { <data> }";
}

/// <summary>The transcription features a route offers, for single requests and for streaming.</summary>
public sealed record SpeechTranscriptionCapabilities
{
    /// <summary>The features offered for single transcription requests.</summary>
    public SpeechTranscriptionModeCapabilities Transcription { get; init => field = SpeechData.Required(value); } = new();
    /// <summary>The features offered for streaming sessions.</summary>
    public SpeechTranscriptionModeCapabilities Streaming { get; init => field = SpeechData.Required(value); } = new();
}

/// <summary>The transcription features offered in one mode.</summary>
public sealed record SpeechTranscriptionModeCapabilities
{
    /// <summary>Whether the mode is available at all.</summary>
    public bool Available { get; init; }
    /// <summary>The kinds of audio input the mode accepts.</summary>
    public IReadOnlySet<InferenceMediaSourceKind> InputKinds { get; init => field = SpeechData.Set(value); } = FrozenSet<InferenceMediaSourceKind>.Empty;
    /// <summary>The audio encodings the mode accepts.</summary>
    public IReadOnlySet<SpeechAudioEncoding> Encodings { get; init => field = SpeechData.Set(value); } = FrozenSet<SpeechAudioEncoding>.Empty;
    /// <summary>The sample rates the mode accepts, or null when unrestricted or unknown.</summary>
    public IReadOnlyList<int>? SampleRates { get; init => field = SpeechData.CopyOptional(value); }
    /// <summary>The languages the mode accepts, or null when unrestricted or unknown.</summary>
    public IReadOnlyList<string>? Languages { get; init => field = SpeechData.CopyOptional(value); }
    /// <summary>The most audio channels the mode accepts, when limited.</summary>
    public int? MaximumChannels { get; init; }
    /// <summary>Whether a spoken language can be specified.</summary>
    public InferenceSupport SupportsLanguage { get; init; }
    /// <summary>Whether channels can be transcribed separately.</summary>
    public InferenceSupport SupportsMultichannel { get; init; }
    /// <summary>Whether words can be labelled with speakers.</summary>
    public InferenceSupport SupportsDiarization { get; init; }
    /// <summary>Whether key terms can bias recognition.</summary>
    public InferenceSupport SupportsKeyTerms { get; init; }
    /// <summary>Whether filler words can be kept.</summary>
    public InferenceSupport SupportsFillerWords { get; init; }
    /// <summary>Whether a voice activity detection threshold can be set.</summary>
    public InferenceSupport SupportsVadThreshold { get; init; }
    /// <summary>Whether text normalization can be controlled.</summary>
    public InferenceSupport SupportsTextNormalization { get; init; }
    /// <summary>Whether partial transcripts can be emitted.</summary>
    public InferenceSupport SupportsInterimResults { get; init; }
    /// <summary>Whether the silence that ends an utterance can be set.</summary>
    public InferenceSupport SupportsEndpointing { get; init; }
    /// <summary>Whether the provider can decide end of turn with a confidence threshold.</summary>
    public InferenceSupport SupportsSmartTurn { get; init; }
    /// <summary>Whether the session supports <see cref="ISpeechTranscriptionSession.FinalizeAsync"/>.</summary>
    public InferenceSupport SupportsFinalize { get; init; }
}

/// <summary>Hosted audio session. Commands finish after ordered send, not provider acknowledgment.</summary>
public interface ISpeechTranscriptionSession : IAsyncDisposable
{
    /// <summary>Sends a chunk of audio to the session.</summary>
    Task AppendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default);
    /// <summary>Asks the provider to finalize the transcript for audio sent so far, for one channel or for all channels when null.</summary>
    Task FinalizeAsync(int? channel = null, CancellationToken cancellationToken = default);
    /// <summary>Signals that no more audio follows.</summary>
    Task CompleteAudioAsync(CancellationToken cancellationToken = default);
    /// <summary>The events the session emits, in order.</summary>
    IAsyncEnumerable<SpeechTranscriptEvent> Events { get; }
}

/// <summary>Hosted transcription operations, separate from Tensor's model-instance STT lifecycle.</summary>
public interface ISpeechTranscriptionClient : IDisposable, IAsyncDisposable
{
    /// <summary>Returns the transcription features offered for a model, or for the default model when null.</summary>
    Task<SpeechTranscriptionCapabilities> GetCapabilitiesAsync(string? modelId = null,
        CancellationToken cancellationToken = default);
    /// <summary>Transcribes one audio input and returns the result.</summary>
    Task<SpeechTranscriptionResult> TranscribeAsync(SpeechTranscriptionInput input,
        SpeechTranscriptionOptions options, CancellationToken cancellationToken = default);
    /// <summary>Opens a streaming transcription session with the given options and caller limits.</summary>
    Task<ISpeechTranscriptionSession> OpenStreamAsync(SpeechStreamOptions options,
        InferenceStreamLimits limits, CancellationToken cancellationToken = default);
    /// <summary>One ephemeral session and owned input enumerator, with explicit caller budgets.</summary>
    IAsyncEnumerable<SpeechTranscriptEvent> TranscribeStreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audio, SpeechStreamOptions options,
        InferenceStreamLimits limits, CancellationToken cancellationToken = default);
}

internal static class SpeechData
{
    internal static T Required<T>(T value) where T : class => value ?? throw new ArgumentNullException(nameof(value));
    internal static double? Finite(double? value) => value is null || double.IsFinite(value.Value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    internal static T? Defined<T>(T? value) where T : struct, Enum => value is null || Enum.IsDefined(value.Value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    internal static ReadOnlyCollection<T>? CopyOptional<T>(IReadOnlyList<T>? value)
    {
        if (value is null) return null;
        T[] items = value.ToArray();
        if (items.Any(item => item is null)) throw new ArgumentException("Speech collections cannot contain null items.", nameof(value));
        return Array.AsReadOnly(items);
    }
    internal static FrozenSet<T> Set<T>(IReadOnlySet<T> value) where T : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (T item in value) if (!Enum.IsDefined(item)) throw new ArgumentOutOfRangeException(nameof(value));
        return value.ToFrozenSet();
    }
}
