using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.TTS;

public sealed record TtsTimestamp(string Text, TimeSpan Start, TimeSpan End)
{
    [JsonRequired] public string Text { get; init => field = TtsData.Required(value); } = TtsData.Required(Text);
    [JsonRequired] public TimeSpan Start { get; init; } = Start;
    [JsonRequired] public TimeSpan End { get; init; } = End;
    public override string ToString() => "TtsTimestamp { <data> }";
}

public sealed record XaiTtsOptions(int? OptimizeStreamingLatency = null,
    bool? TextNormalization = null, IReadOnlyDictionary<string, string>? Replace = null)
{
    public IReadOnlyDictionary<string, string>? Replace { get; init => field = TtsData.Copy(value); } = TtsData.Copy(Replace);
    public override string ToString() => "XaiTtsOptions { <data> }";
}

public sealed record TtsStreamOptions(string? VoiceId = null, double? Speed = null,
    TtsAudioFormat Format = TtsAudioFormat.Wav, string? Language = null,
    int? SampleRate = null, int? BitRate = null, bool? IncludeTimestamps = null,
    XaiTtsOptions? Xai = null)
{
    public override string ToString() => "TtsStreamOptions { <data> }";
}

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

public sealed record TtsAudioChunk(ReadOnlyMemory<byte> Audio, string? ContentType = null,
    TimeSpan? Duration = null, IReadOnlyList<TtsTimestamp>? Timestamps = null) : TtsStreamEvent
{
    [JsonRequired] public ReadOnlyMemory<byte> Audio { get; init => field = value.ToArray(); } = Audio.ToArray();
    public IReadOnlyList<TtsTimestamp>? Timestamps { get; init => field = value is null ? null : TtsData.Copy(value); } = Timestamps is null ? null : TtsData.Copy(Timestamps);
    public override string ToString() => "TtsAudioChunk { <data> }";
}

public sealed record TtsUtteranceCompleted(string? ProviderTraceId = null) : TtsStreamEvent
{
    public InferenceResourceDescriptor? Resource { get; init; }
    public override string ToString() => "TtsUtteranceCompleted { <data> }";
}

public sealed record TtsReplacementsUpdated(IReadOnlyDictionary<string, string> Replace) : TtsStreamEvent
{
    [JsonRequired] public IReadOnlyDictionary<string, string> Replace { get; init => field = TtsData.Copy(TtsData.Required(value))!; } = TtsData.Copy(TtsData.Required(Replace))!;
    public override string ToString() => "TtsReplacementsUpdated { <data> }";
}

public sealed record TtsUtteranceCleared : TtsStreamEvent
{
    public override string ToString() => "TtsUtteranceCleared { <data> }";
}

public sealed record TtsStreamError(InferenceResourceFailure Error) : TtsStreamEvent
{
    [JsonRequired] public InferenceResourceFailure Error { get; init => field = TtsData.Required(value); } = TtsData.Required(Error);
    public override string ToString() => "TtsStreamError { <data> }";
}

/// <summary>One reusable streaming owner. Commands complete after ordered send, not acknowledgment.</summary>
public interface ITtsStreamingSession : IAsyncDisposable
{
    Task AppendTextAsync(string text, CancellationToken cancellationToken = default);
    Task CompleteUtteranceAsync(CancellationToken cancellationToken = default);
    Task UpdateReplacementsAsync(IReadOnlyDictionary<string, string> replacements, CancellationToken cancellationToken = default);
    Task ClearUtteranceAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<TtsStreamEvent> Events { get; }
}

public interface ITtsStreamingClient
{
    Task<ITtsStreamingSession> OpenStreamAsync(TtsStreamOptions options,
        InferenceStreamLimits limits, CancellationToken cancellationToken = default);
    /// <summary>Opens and retires one ephemeral session using the supplied explicit budgets.</summary>
    IAsyncEnumerable<TtsStreamEvent> SpeakStreamAsync(IAsyncEnumerable<string> text,
        TtsStreamOptions options, InferenceStreamLimits limits,
        CancellationToken cancellationToken = default);
}

internal static class TtsData
{
    internal static XaiTtsOptions? EffectiveOptions(XaiTtsOptions? defaults, XaiTtsOptions? options) =>
        defaults is null && options is null ? null : new(
            options?.OptimizeStreamingLatency ?? defaults?.OptimizeStreamingLatency,
            options?.TextNormalization ?? defaults?.TextNormalization,
            options?.Replace ?? defaults?.Replace);
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
