using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.STT;

public enum SpeechAudioEncoding { Pcm16LittleEndian = 0, Mulaw = 1, Alaw = 2, Opus = 3 }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(BinarySpeechInput), "binary")]
[JsonDerivedType(typeof(UrlSpeechInput), "url")]
[JsonDerivedType(typeof(ResourceSpeechInput), "resource")]
public abstract record SpeechTranscriptionInput
{
    private protected SpeechTranscriptionInput() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BinarySpeechInput(string ContentType, ReadOnlyMemory<byte> Data,
    string? FileName = null) : SpeechTranscriptionInput
{
    [JsonRequired] public string ContentType { get; init => field = SpeechData.Required(value); } = SpeechData.Required(ContentType);
    [JsonRequired] public ReadOnlyMemory<byte> Data { get; init => field = value.ToArray(); } = Data.ToArray();
    public override string ToString() => "BinarySpeechInput { <data> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UrlSpeechInput(Uri Url) : SpeechTranscriptionInput
{
    [JsonRequired] public Uri Url { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Url);
    public override string ToString() => "UrlSpeechInput { <data> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResourceSpeechInput(InferenceResourceReference Resource) : SpeechTranscriptionInput
{
    [JsonRequired] public InferenceResourceReference Resource { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Resource);
    public override string ToString() => "ResourceSpeechInput { <data> }";
}

public sealed record SpeechTranscriptionOptions(string? ModelId = null,
    string? Language = null, SpeechAudioEncoding? RawAudioFormat = null,
    int? SampleRate = null, bool? Diarization = null, bool? Multichannel = null,
    int? Channels = null, IReadOnlyList<string>? KeyTerms = null,
    bool? IncludeFillerWords = null, double? VadThreshold = null,
    bool? TextNormalization = null)
{
    public SpeechAudioEncoding? RawAudioFormat { get; init => field = SpeechData.Defined(value); } = SpeechData.Defined(RawAudioFormat);
    public IReadOnlyList<string>? KeyTerms { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(KeyTerms);
    public double? VadThreshold { get; init => field = SpeechData.Finite(value); } = SpeechData.Finite(VadThreshold);
    public override string ToString() => "SpeechTranscriptionOptions { <data> }";
}

public sealed record SpeechStreamOptions(string? ModelId = null, string? Language = null,
    SpeechAudioEncoding? Encoding = null, int? SampleRate = null,
    bool? Diarization = null, bool? Multichannel = null, int? Channels = null,
    IReadOnlyList<string>? KeyTerms = null, bool? IncludeFillerWords = null,
    double? VadThreshold = null, bool? TextNormalization = null, bool? InterimResults = null,
    TimeSpan? Endpointing = null, double? SmartTurnThreshold = null,
    TimeSpan? SmartTurnTimeout = null)
{
    public SpeechAudioEncoding? Encoding { get; init => field = SpeechData.Defined(value); } = SpeechData.Defined(Encoding);
    public IReadOnlyList<string>? KeyTerms { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(KeyTerms);
    public double? VadThreshold { get; init => field = SpeechData.Finite(value); } = SpeechData.Finite(VadThreshold);
    public double? SmartTurnThreshold { get; init => field = SpeechData.Finite(value); } = SpeechData.Finite(SmartTurnThreshold);
    public override string ToString() => "SpeechStreamOptions { <data> }";
}

public sealed record SpeechWord(string Text, TimeSpan Start, TimeSpan End,
    double? Confidence = null, int? Speaker = null)
{
    [JsonRequired] public string Text { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Text);
    [JsonRequired] public TimeSpan Start { get; init; } = Start;
    [JsonRequired] public TimeSpan End { get; init; } = End;
    public double? Confidence { get; init => field = SpeechData.Finite(value); } = SpeechData.Finite(Confidence);
    public override string ToString() => "SpeechWord { <data> }";
}

public sealed record SpeechChannelTranscript(int Index, string Text,
    string? Language = null, IReadOnlyList<SpeechWord>? Words = null)
{
    [JsonRequired] public int Index { get; init; } = Index;
    [JsonRequired] public string Text { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Text);
    public IReadOnlyList<SpeechWord>? Words { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(Words);
    public override string ToString() => "SpeechChannelTranscript { <data> }";
}

public sealed record SpeechTranscriptionResult(string Text,
    InferenceProvenance Provenance, string? Language = null, TimeSpan? Duration = null,
    IReadOnlyList<SpeechWord>? Words = null,
    IReadOnlyList<SpeechChannelTranscript>? Channels = null)
{
    [JsonRequired] public string Text { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Text);
    [JsonRequired] public InferenceProvenance Provenance { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Provenance);
    public IReadOnlyList<SpeechWord>? Words { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(Words);
    public IReadOnlyList<SpeechChannelTranscript>? Channels { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(Channels);
    public override string ToString() => "SpeechTranscriptionResult { <data> }";
}

public abstract record SpeechTranscriptEvent
{
    private protected SpeechTranscriptEvent() { }
}

public sealed record SpeechTranscriptCreatedEvent(string? SessionId = null) : SpeechTranscriptEvent
{
    public override string ToString() => "SpeechTranscriptCreatedEvent { <data> }";
}

public sealed record SpeechTranscriptPartialEvent(string Text, TimeSpan? Start = null,
    TimeSpan? Duration = null, bool? IsFinal = null, bool? SpeechFinal = null,
    int? ChannelIndex = null, IReadOnlyList<SpeechWord>? Words = null,
    double? EndOfTurnConfidence = null) : SpeechTranscriptEvent
{
    [JsonRequired] public string Text { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Text);
    public IReadOnlyList<SpeechWord>? Words { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(Words);
    public double? EndOfTurnConfidence { get; init => field = SpeechData.Finite(value); } = SpeechData.Finite(EndOfTurnConfidence);
    public override string ToString() => "SpeechTranscriptPartialEvent { <data> }";
}

public sealed record SpeechTranscriptDoneEvent(string Text, TimeSpan? Duration = null,
    int? ChannelIndex = null, IReadOnlyList<SpeechWord>? Words = null) : SpeechTranscriptEvent
{
    [JsonRequired] public string Text { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Text);
    public IReadOnlyList<SpeechWord>? Words { get; init => field = SpeechData.CopyOptional(value); } = SpeechData.CopyOptional(Words);
    public override string ToString() => "SpeechTranscriptDoneEvent { <data> }";
}

public sealed record SpeechTranscriptErrorEvent(InferenceResourceFailure Error) : SpeechTranscriptEvent
{
    [JsonRequired] public InferenceResourceFailure Error { get; init => field = SpeechData.Required(value); } = SpeechData.Required(Error);
    public override string ToString() => "SpeechTranscriptErrorEvent { <data> }";
}

public sealed record SpeechTranscriptionCapabilities
{
    public SpeechTranscriptionModeCapabilities Transcription { get; init => field = SpeechData.Required(value); } = new();
    public SpeechTranscriptionModeCapabilities Streaming { get; init => field = SpeechData.Required(value); } = new();
}

public sealed record SpeechTranscriptionModeCapabilities
{
    public bool Available { get; init; }
    public IReadOnlySet<InferenceMediaSourceKind> InputKinds { get; init => field = SpeechData.Set(value); } = FrozenSet<InferenceMediaSourceKind>.Empty;
    public IReadOnlySet<SpeechAudioEncoding> Encodings { get; init => field = SpeechData.Set(value); } = FrozenSet<SpeechAudioEncoding>.Empty;
    public IReadOnlyList<int>? SampleRates { get; init => field = SpeechData.CopyOptional(value); }
    public IReadOnlyList<string>? Languages { get; init => field = SpeechData.CopyOptional(value); }
    public int? MaximumChannels { get; init; }
    public bool SupportsLanguage { get; init; }
    public bool SupportsMultichannel { get; init; }
    public bool SupportsDiarization { get; init; }
    public bool SupportsKeyTerms { get; init; }
    public bool SupportsFillerWords { get; init; }
    public bool SupportsVadThreshold { get; init; }
    public bool SupportsTextNormalization { get; init; }
    public bool SupportsInterimResults { get; init; }
    public bool SupportsEndpointing { get; init; }
    public bool SupportsSmartTurn { get; init; }
    public bool SupportsFinalize { get; init; }
}

/// <summary>Hosted audio session. Commands finish after ordered send, not provider acknowledgment.</summary>
public interface ISpeechTranscriptionSession : IAsyncDisposable
{
    Task AppendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default);
    Task FinalizeAsync(int? channel = null, CancellationToken cancellationToken = default);
    Task CompleteAudioAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<SpeechTranscriptEvent> Events { get; }
}

/// <summary>Hosted transcription operations, separate from Tensor's model-instance STT lifecycle.</summary>
public interface ISpeechTranscriptionClient
{
    Task<SpeechTranscriptionCapabilities> GetCapabilitiesAsync(string? modelId = null,
        CancellationToken cancellationToken = default);
    Task<SpeechTranscriptionResult> TranscribeAsync(SpeechTranscriptionInput input,
        SpeechTranscriptionOptions options, CancellationToken cancellationToken = default);
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
