using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.TTS;

/// <summary>Time span of one piece of text within synthesized audio.</summary>
/// <param name="Text">Text that the span covers.</param>
/// <param name="Start">Offset where the text starts in the audio.</param>
/// <param name="End">Offset where the text ends in the audio.</param>
public sealed record TtsTimestamp(string Text, TimeSpan Start, TimeSpan End)
{
    /// <summary>Text that the span covers. Setting null throws.</summary>
    [JsonRequired] public string Text { get; init => field = TtsData.Required(value); } = TtsData.Required(Text);
    /// <summary>Offset where the text starts in the audio.</summary>
    [JsonRequired] public TimeSpan Start { get; init; } = Start;
    /// <summary>Offset where the text ends in the audio.</summary>
    [JsonRequired] public TimeSpan End { get; init; } = End;
    /// <inheritdoc />
    public override string ToString() => "TtsTimestamp { <data> }";
}

/// <summary>Settings for a streaming text-to-speech session. A null value leaves that setting to the provider.</summary>
/// <param name="VoiceId">Provider voice identifier.</param>
/// <param name="Speed">Speech rate multiplier.</param>
/// <param name="Format">Audio format of the synthesized output.</param>
/// <param name="Language">Language of the input text.</param>
/// <param name="SampleRate">Output sample rate in hertz.</param>
/// <param name="BitRate">Output bit rate for compressed formats.</param>
/// <param name="IncludeTimestamps">True to request <see cref="TtsTimestamp"/> entries on audio chunks.</param>
public sealed record TtsStreamOptions(string? VoiceId = null, double? Speed = null,
    TtsAudioFormat Format = TtsAudioFormat.Wav, string? Language = null,
    int? SampleRate = null, int? BitRate = null, bool? IncludeTimestamps = null)
{
    /// <inheritdoc />
    public override string ToString() => "TtsStreamOptions { <data> }";
}

/// <summary>Event a streaming text-to-speech session publishes.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TtsAudioChunk), "audio")]
[JsonDerivedType(typeof(TtsUtteranceCompleted), "utterance-completed")]
[JsonDerivedType(typeof(TtsStreamError), "error")]
[JsonDerivedType(typeof(TtsReplacementsUpdated), "replacements-updated")]
[JsonDerivedType(typeof(TtsUtteranceCleared), "utterance-cleared")]
public abstract record TtsStreamEvent
{
    private protected TtsStreamEvent() { }
}

/// <summary>Chunk of synthesized audio.</summary>
/// <param name="Audio">Encoded audio bytes.</param>
/// <param name="ContentType">Media type of the audio, or null when not reported.</param>
/// <param name="Duration">Playback duration of the chunk, or null when not reported.</param>
/// <param name="Timestamps">Text spans within the chunk, or null when timestamps were not requested or not reported.</param>
public sealed record TtsAudioChunk(ReadOnlyMemory<byte> Audio, string? ContentType = null,
    TimeSpan? Duration = null, IReadOnlyList<TtsTimestamp>? Timestamps = null) : TtsStreamEvent
{
    /// <summary>Encoded audio bytes. The setter stores a copy.</summary>
    [JsonRequired] public ReadOnlyMemory<byte> Audio { get; init => field = value.ToArray(); } = Audio.ToArray();
    /// <summary>Text spans within the chunk, or null when not reported. The setter stores a copy and throws on a null entry.</summary>
    public IReadOnlyList<TtsTimestamp>? Timestamps { get; init => field = value is null ? null : TtsData.Copy(value); } = Timestamps is null ? null : TtsData.Copy(Timestamps);
    /// <inheritdoc />
    public override string ToString() => "TtsAudioChunk { <data> }";
}

/// <summary>Event that marks the end of the audio for the current utterance.</summary>
/// <param name="ProviderTraceId">Provider trace identifier for the utterance, or null when not reported.</param>
public sealed record TtsUtteranceCompleted(string? ProviderTraceId = null) : TtsStreamEvent
{
    /// <summary>Stored audio resource committed for the utterance, or null when the session does not store audio.</summary>
    public InferenceResourceDescriptor? Resource { get; init; }
    /// <inheritdoc />
    public override string ToString() => "TtsUtteranceCompleted { <data> }";
}

/// <summary>Event that confirms the text replacement map the provider now applies.</summary>
/// <param name="Replace">Map from source text to the text spoken in its place.</param>
public sealed record TtsReplacementsUpdated(IReadOnlyDictionary<string, string> Replace) : TtsStreamEvent
{
    /// <summary>Map from source text to the text spoken in its place. The setter stores an ordinal copy and throws on a null map, key or value.</summary>
    [JsonRequired] public IReadOnlyDictionary<string, string> Replace { get; init => field = TtsData.Copy(TtsData.Required(value))!; } = TtsData.Copy(TtsData.Required(Replace))!;
    /// <inheritdoc />
    public override string ToString() => "TtsReplacementsUpdated { <data> }";
}

/// <summary>Event that confirms the provider discarded the pending utterance.</summary>
public sealed record TtsUtteranceCleared : TtsStreamEvent
{
    /// <inheritdoc />
    public override string ToString() => "TtsUtteranceCleared { <data> }";
}

/// <summary>Event that reports a stream failure.</summary>
/// <param name="Error">Failure the session reports.</param>
public sealed record TtsStreamError(InferenceResourceFailure Error) : TtsStreamEvent
{
    /// <summary>Failure the session reports. Setting null throws.</summary>
    [JsonRequired] public InferenceResourceFailure Error { get; init => field = TtsData.Required(value); } = TtsData.Required(Error);
    /// <inheritdoc />
    public override string ToString() => "TtsStreamError { <data> }";
}

/// <summary>One reusable streaming owner. Commands complete after ordered send, not acknowledgment.</summary>
public interface ITtsStreamingSession : IAsyncDisposable
{
    /// <summary>Appends text to the current utterance.</summary>
    Task AppendTextAsync(string text, CancellationToken cancellationToken = default);
    /// <summary>Marks the current utterance complete so the provider finishes its audio.</summary>
    Task CompleteUtteranceAsync(CancellationToken cancellationToken = default);
    /// <summary>Sends a new text replacement map, from source text to the text spoken in its place.</summary>
    Task UpdateReplacementsAsync(IReadOnlyDictionary<string, string> replacements, CancellationToken cancellationToken = default);
    /// <summary>Discards the text of the current utterance.</summary>
    Task ClearUtteranceAsync(CancellationToken cancellationToken = default);
    /// <summary>Events the session publishes, in arrival order.</summary>
    IAsyncEnumerable<TtsStreamEvent> Events { get; }
}

/// <summary>Client that opens streaming text-to-speech sessions.</summary>
public interface ITtsStreamingClient
{
    /// <summary>Opens a reusable streaming session with the given settings and explicit budgets.</summary>
    Task<ITtsStreamingSession> OpenStreamAsync(TtsStreamOptions options,
        InferenceStreamLimits limits, CancellationToken cancellationToken = default);
    /// <summary>Opens and retires one ephemeral session using the supplied explicit budgets.</summary>
    IAsyncEnumerable<TtsStreamEvent> SpeakStreamAsync(IAsyncEnumerable<string> text,
        TtsStreamOptions options, InferenceStreamLimits limits,
        CancellationToken cancellationToken = default);
}

internal static class TtsData
{
    internal static T Required<T>(T value) where T : class => value ?? throw new ArgumentNullException(nameof(value));

    internal static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        T[] items = value.ToArray();
        if (items.Any(item => item is null)) throw new ArgumentException("Items cannot be null.", nameof(value));
        return Array.AsReadOnly(items);
    }

    internal static ReadOnlyDictionary<string, string>? Copy(IReadOnlyDictionary<string, string>? value, StringComparer? comparer = null)
    {
        if (value is null) return null;
        var items = new Dictionary<string, string>(comparer ?? StringComparer.Ordinal);
        foreach ((string key, string item) in value) items.Add(Required(key), Required(item));
        return new(items);
    }
}
