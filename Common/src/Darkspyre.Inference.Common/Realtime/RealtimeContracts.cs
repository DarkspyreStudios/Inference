using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;

namespace Darkspyre.Inference.Common.Realtime;

public enum RealtimeAudioEncoding { Pcm16LittleEndian = 0, Mulaw = 1, Alaw = 2, Opus = 3 }
public enum RealtimeAudioTransport { Json = 0, Binary = 1 }
public enum RealtimeModality { Text = 0, Audio = 1 }
public enum RealtimeMessageRole { User = 0, Assistant = 1, System = 2 }

public sealed record RealtimeAudioConfig(RealtimeAudioEncoding Encoding,
    int? SampleRate = null, RealtimeAudioTransport? Transport = null)
{
    [JsonRequired] public RealtimeAudioEncoding Encoding { get; init => field = RealtimeData.Defined(value); } = RealtimeData.Defined(Encoding);
    public RealtimeAudioTransport? Transport { get; init => field = value is { } transport ? RealtimeData.Defined(transport) : null; }
        = Transport is { } transport ? RealtimeData.Defined(transport) : null;
    public override string ToString() => "RealtimeAudioConfig { <configuration> }";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RealtimeManualTurnDetection), "manual")]
[JsonDerivedType(typeof(RealtimeServerVad), "server-vad")]
public abstract record RealtimeTurnDetection
{
    private protected RealtimeTurnDetection() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeManualTurnDetection : RealtimeTurnDetection;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeServerVad(double? Threshold = null, TimeSpan? SilenceDuration = null,
    TimeSpan? PrefixPadding = null, TimeSpan? IdleTimeout = null) : RealtimeTurnDetection;

public sealed record RealtimeConversationReference(string ConversationId, InferenceResourceScope Scope)
{
    [JsonRequired] public string ConversationId { get; init => field = RealtimeData.Identity(value); } = RealtimeData.Identity(ConversationId);
    [JsonRequired] public InferenceResourceScope Scope { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Scope);
    public override string ToString() => "RealtimeConversationReference { <receipt> }";
}

public sealed record RealtimeResumptionOptions([property: JsonRequired] bool Enabled,
    RealtimeConversationReference? Conversation = null)
{
    public override string ToString() => "RealtimeResumptionOptions { <options> }";
}

public abstract record RealtimeProviderOptions
{
    protected RealtimeProviderOptions() { }
    public override string ToString() => "RealtimeProviderOptions { <options> }";
}

public sealed record RealtimeSessionConfig
{
    public string? ModelId { get; init; }
    public string? Instructions { get; init; }
    public string? Voice { get; init; }
    public LlmReasoningLevel? ReasoningEffort { get; init; }
    public IReadOnlyList<LlmToolDefinition>? Tools { get; init => field = value is null ? null : RealtimeData.Copy(value); }
    public IReadOnlyList<LlmHostedTool>? HostedTools { get; init => field = value is null ? null : RealtimeData.Copy(value); }
    public RealtimeTurnDetection? TurnDetection { get; init; }
    public RealtimeAudioConfig? InputAudio { get; init; }
    public RealtimeAudioConfig? OutputAudio { get; init; }
    public string? TranscriptionLanguage { get; init; }
    public IReadOnlyList<string>? TranscriptionKeyTerms { get; init => field = value is null ? null : RealtimeData.Copy(value); }
    public double? OutputSpeed { get; init; }
    public RealtimeResumptionOptions? Resumption { get; init; }
    public RealtimeProviderOptions? ProviderOptions { get; init; }
    public override string ToString() => "RealtimeSessionConfig { <configuration> }";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RealtimeTextContent), "text")]
[JsonDerivedType(typeof(RealtimeAudioContent), "audio")]
public abstract record RealtimeContent
{
    private protected RealtimeContent() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeTextContent(string Text) : RealtimeContent
{
    [JsonRequired] public string Text { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Text);
    public override string ToString() => "RealtimeTextContent { <content> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeAudioContent(ReadOnlyMemory<byte> Audio, string? Transcript = null) : RealtimeContent
{
    [JsonRequired] public ReadOnlyMemory<byte> Audio { get; init => field = value.ToArray(); } = Audio.ToArray();
    public override string ToString() => "RealtimeAudioContent { <content> }";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RealtimeMessageItem), "message")]
[JsonDerivedType(typeof(RealtimeFunctionCallItem), "function-call")]
[JsonDerivedType(typeof(RealtimeFunctionOutputItem), "function-output")]
public abstract record RealtimeConversationItem
{
    protected RealtimeConversationItem() { }
    public string? Id { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeMessageItem(RealtimeMessageRole Role, IReadOnlyList<RealtimeContent> Content) : RealtimeConversationItem
{
    [JsonRequired] public RealtimeMessageRole Role { get; init => field = RealtimeData.Defined(value); } = RealtimeData.Defined(Role);
    [JsonRequired] public IReadOnlyList<RealtimeContent> Content { get; init => field = RealtimeData.Copy(value); } = RealtimeData.Copy(Content);
    public override string ToString() => "RealtimeMessageItem { <item> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeFunctionCallItem(string Name, string ArgumentsJson, string? CallId = null) : RealtimeConversationItem
{
    [JsonRequired] public string Name { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Name);
    [JsonRequired] public string ArgumentsJson { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(ArgumentsJson);
    public override string ToString() => "RealtimeFunctionCallItem { <item> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeFunctionOutputItem(string CallId, LlmToolOutput Output) : RealtimeConversationItem
{
    [JsonRequired] public string CallId { get; init => field = RealtimeData.Identity(value); } = RealtimeData.Identity(CallId);
    [JsonRequired] public LlmToolOutput Output { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Output);
    public override string ToString() => "RealtimeFunctionOutputItem { <item> }";
}

public sealed record RealtimeResponseOptions(string? Instructions = null,
    IReadOnlyList<RealtimeModality>? Modalities = null, IReadOnlyDictionary<string, string>? Metadata = null)
{
    public IReadOnlyList<RealtimeModality>? Modalities { get; init => field = value is null ? null : RealtimeData.Copy(value); }
        = Modalities is null ? null : RealtimeData.Copy(Modalities);
    public IReadOnlyDictionary<string, string>? Metadata { get; init => field = RealtimeData.Map(value); } = RealtimeData.Map(Metadata);
    public override string ToString() => "RealtimeResponseOptions { <options> }";
}

public sealed record RealtimeToolResult(string CallId, LlmToolOutput Output)
{
    [JsonRequired] public string CallId { get; init => field = RealtimeData.Identity(value); } = RealtimeData.Identity(CallId);
    [JsonRequired] public LlmToolOutput Output { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Output);
    public override string ToString() => "RealtimeToolResult { <result> }";
}

public sealed record RealtimeCapabilities
{
    public IReadOnlySet<RealtimeAudioEncoding> InputEncodings { get; init => field = RealtimeData.Set(value); } = FrozenSet<RealtimeAudioEncoding>.Empty;
    public IReadOnlySet<RealtimeAudioEncoding> OutputEncodings { get; init => field = RealtimeData.Set(value); } = FrozenSet<RealtimeAudioEncoding>.Empty;
    public IReadOnlySet<LlmHostedToolKind> SupportedHostedToolKinds { get; init => field = RealtimeData.Set(value); } = FrozenSet<LlmHostedToolKind>.Empty;
    public IReadOnlyList<int>? InputSampleRates { get; init => field = value is null ? null : RealtimeData.Copy(value); }
    public IReadOnlyList<int>? OutputSampleRates { get; init => field = value is null ? null : RealtimeData.Copy(value); }
    public bool SupportsManualTurns { get; init; }
    public bool SupportsServerVad { get; init; }
    public bool SupportsClientTools { get; init; }
    public bool SupportsStructuredToolOutput { get; init; }
    public bool SupportsResumption { get; init; }
    public bool SupportsBinaryTransport { get; init; }
    public bool SupportsPronunciationReplacement { get; init; }
    public bool SupportsForcedMessages { get; init; }
}

/// <summary>One explicit session owner. Command completion means ordered send, not event consumption.</summary>
public interface IRealtimeVoiceSession : IAsyncDisposable
{
    Task UpdateSessionAsync(RealtimeSessionConfig config, CancellationToken cancellationToken = default);
    Task AppendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default);
    Task CommitAudioAsync(CancellationToken cancellationToken = default);
    Task ClearAudioAsync(CancellationToken cancellationToken = default);
    Task AddItemAsync(RealtimeConversationItem item, string? previousItemId = null, CancellationToken cancellationToken = default);
    Task DeleteItemAsync(string itemId, CancellationToken cancellationToken = default);
    Task TruncateItemAsync(string itemId, int contentIndex, TimeSpan audioEnd, CancellationToken cancellationToken = default);
    Task CreateResponseAsync(RealtimeResponseOptions? options = null, CancellationToken cancellationToken = default);
    Task CancelResponseAsync(string? responseId = null, CancellationToken cancellationToken = default);
    Task SubmitToolResultAsync(RealtimeToolResult result, CancellationToken cancellationToken = default);
    IAsyncEnumerable<RealtimeEvent> Events { get; }
}

internal static class RealtimeData
{
    internal static T Required<T>(T? value) where T : class { ArgumentNullException.ThrowIfNull(value); return value; }
    internal static string Identity(string value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value; }
    internal static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    internal static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        T[] copy = value.ToArray();
        if (copy.Any(item => item is null)) throw new ArgumentException("Realtime collections cannot contain null entries.", nameof(value));
        return Array.AsReadOnly(copy);
    }
    internal static IReadOnlySet<T> Set<T>(IReadOnlySet<T> value) where T : struct, Enum
    { ArgumentNullException.ThrowIfNull(value); return value.Select(Defined).ToFrozenSet(); }
    internal static IReadOnlyDictionary<string, string>? Map(IReadOnlyDictionary<string, string>? value)
    {
        if (value is null) return null;
        return new ReadOnlyDictionary<string, string>(value.ToDictionary(pair => Required(pair.Key), pair => Required(pair.Value), StringComparer.Ordinal));
    }
}
