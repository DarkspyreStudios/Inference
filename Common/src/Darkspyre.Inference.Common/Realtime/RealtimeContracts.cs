using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;

namespace Darkspyre.Inference.Common.Realtime;

/// <summary>Audio sample encoding of a realtime input or output stream: 16-bit little-endian PCM, mu-law, A-law or Opus.</summary>
public enum RealtimeAudioEncoding
{
    /// <summary>Uncompressed 16-bit signed samples in little-endian byte order.</summary>
    Pcm16LittleEndian = 0,
    /// <summary>8-bit G.711 mu-law companded samples.</summary>
    Mulaw = 1,
    /// <summary>8-bit G.711 A-law companded samples.</summary>
    Alaw = 2,
    /// <summary>Opus-compressed audio frames.</summary>
    Opus = 3
}
/// <summary>How audio bytes travel on the realtime connection: inside JSON messages or as binary frames.</summary>
public enum RealtimeAudioTransport
{
    /// <summary>Audio bytes travel encoded inside JSON messages.</summary>
    Json = 0,
    /// <summary>Audio bytes travel as raw binary frames.</summary>
    Binary = 1
}
/// <summary>Output kind a realtime response produces: text or audio.</summary>
public enum RealtimeModality
{
    /// <summary>The response produces written output.</summary>
    Text = 0,
    /// <summary>The response produces spoken output.</summary>
    Audio = 1
}
/// <summary>Author of a realtime conversation message: the user, the assistant or the system.</summary>
public enum RealtimeMessageRole
{
    /// <summary>The person talking to the model wrote the message.</summary>
    User = 0,
    /// <summary>The model wrote the message.</summary>
    Assistant = 1,
    /// <summary>The message carries instructions from the application.</summary>
    System = 2
}

/// <summary>Audio format of one direction of a realtime session.</summary>
/// <param name="Encoding">Sample encoding of the audio.</param>
/// <param name="SampleRate">Sample rate in hertz, or null for the provider default.</param>
/// <param name="Transport">How the audio bytes travel on the connection, or null for the provider default.</param>
public sealed record RealtimeAudioConfig(RealtimeAudioEncoding Encoding,
    int? SampleRate = null, RealtimeAudioTransport? Transport = null)
{
    /// <summary>Sample encoding of the audio. Setting an undefined value throws.</summary>
    [JsonRequired] public RealtimeAudioEncoding Encoding { get; init => field = RealtimeData.Defined(value); } = RealtimeData.Defined(Encoding);
    /// <summary>How the audio bytes travel on the connection, or null for the provider default. Setting an undefined value throws.</summary>
    public RealtimeAudioTransport? Transport { get; init => field = value is { } transport ? RealtimeData.Defined(transport) : null; }
        = Transport is { } transport ? RealtimeData.Defined(transport) : null;
    /// <inheritdoc />
    public override string ToString() => "RealtimeAudioConfig { <configuration> }";
}

/// <summary>How the session decides that a user turn has ended: manually or by server voice activity detection.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RealtimeManualTurnDetection), "manual")]
[JsonDerivedType(typeof(RealtimeServerVad), "server-vad")]
public abstract record RealtimeTurnDetection
{
    private protected RealtimeTurnDetection() { }
}

/// <summary>Turn detection in which the caller commits audio and requests responses explicitly.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeManualTurnDetection : RealtimeTurnDetection;

/// <summary>Turn detection in which the server detects the end of user speech from voice activity.</summary>
/// <param name="Threshold">Voice activity detection threshold, or null for the provider default.</param>
/// <param name="SilenceDuration">Silence that ends a turn, or null for the provider default.</param>
/// <param name="PrefixPadding">Audio kept before detected speech starts, or null for the provider default.</param>
/// <param name="IdleTimeout">Idle timeout the provider applies when no speech arrives, or null for none.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeServerVad(double? Threshold = null, TimeSpan? SilenceDuration = null,
    TimeSpan? PrefixPadding = null, TimeSpan? IdleTimeout = null) : RealtimeTurnDetection;

/// <summary>Identifies a provider-side realtime conversation and the resource scope that owns it.</summary>
/// <param name="ConversationId">Provider identifier of the conversation.</param>
/// <param name="Scope">Resource scope the conversation belongs to.</param>
public sealed record RealtimeConversationReference(string ConversationId, InferenceResourceScope Scope)
{
    /// <summary>Provider identifier of the conversation. Setting a null, empty or whitespace value throws.</summary>
    [JsonRequired] public string ConversationId { get; init => field = RealtimeData.Identity(value); } = RealtimeData.Identity(ConversationId);
    /// <summary>Resource scope the conversation belongs to. Setting null throws.</summary>
    [JsonRequired] public InferenceResourceScope Scope { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Scope);
    /// <inheritdoc />
    public override string ToString() => "RealtimeConversationReference { <receipt> }";
}

/// <summary>Session resumption settings.</summary>
/// <param name="Enabled">True to make the session resumable.</param>
/// <param name="Conversation">Existing conversation to resume, or null to start a new one.</param>
public sealed record RealtimeResumptionOptions([property: JsonRequired] bool Enabled,
    RealtimeConversationReference? Conversation = null)
{
    /// <inheritdoc />
    public override string ToString() => "RealtimeResumptionOptions { <options> }";
}

/// <summary>Base type for provider-specific realtime session settings.</summary>
public abstract record RealtimeProviderOptions
{
    /// <summary>Initializes provider-specific settings in a derived type.</summary>
    protected RealtimeProviderOptions() { }
    /// <inheritdoc />
    public override string ToString() => "RealtimeProviderOptions { <options> }";
}

/// <summary>Session settings sent when a realtime session is configured or updated. A null property leaves that setting to the provider.</summary>
public sealed record RealtimeSessionConfig
{
    /// <summary>Provider model identifier.</summary>
    public string? ModelId { get; init; }
    /// <summary>System instructions for the assistant.</summary>
    public string? Instructions { get; init; }
    /// <summary>Provider voice name for spoken output.</summary>
    public string? Voice { get; init; }
    /// <summary>Reasoning effort the model applies.</summary>
    public LlmReasoningLevel? ReasoningEffort { get; init; }
    /// <summary>Client-executed function tools the model can call. Setting a list with a null entry throws.</summary>
    public IReadOnlyList<LlmToolDefinition>? Tools { get; init => field = value is null ? null : RealtimeData.Copy(value); }
    /// <summary>Provider-hosted tools the model can use. Setting a list with a null entry throws.</summary>
    public IReadOnlyList<LlmHostedTool>? HostedTools { get; init => field = value is null ? null : RealtimeData.Copy(value); }
    /// <summary>How the end of a user turn is detected.</summary>
    public RealtimeTurnDetection? TurnDetection { get; init; }
    /// <summary>Format of audio the caller sends.</summary>
    public RealtimeAudioConfig? InputAudio { get; init; }
    /// <summary>Format of audio the session returns.</summary>
    public RealtimeAudioConfig? OutputAudio { get; init; }
    /// <summary>Language of input speech for transcription.</summary>
    public string? TranscriptionLanguage { get; init; }
    /// <summary>Terms that bias input transcription. Setting a list with a null entry throws.</summary>
    public IReadOnlyList<string>? TranscriptionKeyTerms { get; init => field = value is null ? null : RealtimeData.Copy(value); }
    /// <summary>Playback speed multiplier for spoken output.</summary>
    public double? OutputSpeed { get; init; }
    /// <summary>Session resumption settings.</summary>
    public RealtimeResumptionOptions? Resumption { get; init; }
    /// <summary>Provider-specific settings.</summary>
    public RealtimeProviderOptions? ProviderOptions { get; init; }
    /// <inheritdoc />
    public override string ToString() => "RealtimeSessionConfig { <configuration> }";
}

/// <summary>One content part of a realtime conversation message: text or audio.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RealtimeTextContent), "text")]
[JsonDerivedType(typeof(RealtimeAudioContent), "audio")]
public abstract record RealtimeContent
{
    private protected RealtimeContent() { }
}

/// <summary>Text content part of a realtime message.</summary>
/// <param name="Text">Text of the part.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeTextContent(string? Text) : RealtimeContent
{
    /// <summary>Text of the part.</summary>
    [JsonRequired] public string? Text { get; init; } = Text;
    /// <summary>Transcript the provider reports for the part, or null when none.</summary>
    public string? Transcript { get; init; }
    /// <inheritdoc />
    public override string ToString() => "RealtimeTextContent { <content> }";
}

/// <summary>Audio content part of a realtime message.</summary>
/// <param name="Audio">Encoded audio bytes, or null when the part carries only a transcript.</param>
/// <param name="Transcript">Transcript of the audio, or null when none is known.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeAudioContent(ReadOnlyMemory<byte>? Audio, string? Transcript = null) : RealtimeContent
{
    /// <summary>Encoded audio bytes, or null when the part carries only a transcript. The setter stores a copy.</summary>
    [JsonRequired]
    public ReadOnlyMemory<byte>? Audio { get; init => field = value is { } bytes ? bytes.ToArray() : null; }
        = Audio is { } bytes ? bytes.ToArray() : null;
    /// <inheritdoc />
    public override string ToString() => "RealtimeAudioContent { <content> }";
}

/// <summary>One item in a realtime conversation: a message, a function call or a function output.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RealtimeMessageItem), "message")]
[JsonDerivedType(typeof(RealtimeFunctionCallItem), "function-call")]
[JsonDerivedType(typeof(RealtimeFunctionOutputItem), "function-output")]
public abstract record RealtimeConversationItem
{
    /// <summary>Initializes a conversation item in a derived type.</summary>
    protected RealtimeConversationItem() { }
    /// <summary>Item identifier, or null to let the provider assign one.</summary>
    public string? Id { get; init; }
}

/// <summary>Conversation item that holds a message from one role.</summary>
/// <param name="Role">Author of the message.</param>
/// <param name="Content">Content parts of the message.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeMessageItem(RealtimeMessageRole Role, IReadOnlyList<RealtimeContent> Content) : RealtimeConversationItem
{
    /// <summary>Author of the message. Setting an undefined value throws.</summary>
    [JsonRequired] public RealtimeMessageRole Role { get; init => field = RealtimeData.Defined(value); } = RealtimeData.Defined(Role);
    /// <summary>Content parts of the message. The setter stores a copy and throws on a null entry.</summary>
    [JsonRequired] public IReadOnlyList<RealtimeContent> Content { get; init => field = RealtimeData.Copy(value); } = RealtimeData.Copy(Content);
    /// <inheritdoc />
    public override string ToString() => "RealtimeMessageItem { <item> }";
}

/// <summary>Conversation item in which the model calls a client function tool.</summary>
/// <param name="Name">Name of the called function.</param>
/// <param name="ArgumentsJson">Call arguments as a JSON string.</param>
/// <param name="CallId">Identifier that the matching tool result refers to, or null when the provider has not assigned one.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeFunctionCallItem(string? Name, string? ArgumentsJson, string? CallId = null) : RealtimeConversationItem
{
    /// <summary>Name of the called function.</summary>
    [JsonRequired] public string? Name { get; init; } = Name;
    /// <summary>Call arguments as a JSON string.</summary>
    [JsonRequired] public string? ArgumentsJson { get; init; } = ArgumentsJson;
    /// <inheritdoc />
    public override string ToString() => "RealtimeFunctionCallItem { <item> }";
}

/// <summary>Conversation item that holds the output of a client function call.</summary>
/// <param name="CallId">Identifier of the function call this output answers.</param>
/// <param name="Output">Output the tool returned.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealtimeFunctionOutputItem(string CallId, LlmToolOutput Output) : RealtimeConversationItem
{
    /// <summary>Identifier of the function call this output answers. Setting a null, empty or whitespace value throws.</summary>
    [JsonRequired] public string CallId { get; init => field = RealtimeData.Identity(value); } = RealtimeData.Identity(CallId);
    /// <summary>Output the tool returned. Setting null throws.</summary>
    [JsonRequired] public LlmToolOutput Output { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Output);
    /// <inheritdoc />
    public override string ToString() => "RealtimeFunctionOutputItem { <item> }";
}

/// <summary>Per-response settings for an explicitly requested realtime response.</summary>
/// <param name="Instructions">Instructions for this response only, or null to use the session instructions.</param>
/// <param name="Modalities">Output kinds the response produces, or null for the session default.</param>
/// <param name="Metadata">Caller key-value pairs attached to the response, or null for none.</param>
public sealed record RealtimeResponseOptions(string? Instructions = null,
    IReadOnlyList<RealtimeModality>? Modalities = null, IReadOnlyDictionary<string, string>? Metadata = null)
{
    /// <summary>Output kinds the response produces, or null for the session default. The setter stores a copy and throws on a null entry.</summary>
    public IReadOnlyList<RealtimeModality>? Modalities { get; init => field = value is null ? null : RealtimeData.Copy(value); }
        = Modalities is null ? null : RealtimeData.Copy(Modalities);
    /// <summary>Caller key-value pairs attached to the response, or null for none. The setter stores an ordinal copy and throws on a null key or value.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init => field = RealtimeData.Map(value); } = RealtimeData.Map(Metadata);
    /// <inheritdoc />
    public override string ToString() => "RealtimeResponseOptions { <options> }";
}

/// <summary>Result of a client function call that the caller submits to the session.</summary>
/// <param name="CallId">Identifier of the function call this result answers.</param>
/// <param name="Output">Output the tool returned.</param>
public sealed record RealtimeToolResult(string CallId, LlmToolOutput Output)
{
    /// <summary>Identifier of the function call this result answers. Setting a null, empty or whitespace value throws.</summary>
    [JsonRequired] public string CallId { get; init => field = RealtimeData.Identity(value); } = RealtimeData.Identity(CallId);
    /// <summary>Output the tool returned. Setting null throws.</summary>
    [JsonRequired] public LlmToolOutput Output { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Output);
    /// <inheritdoc />
    public override string ToString() => "RealtimeToolResult { <result> }";
}

/// <summary>Realtime features a provider reports as supported, unsupported or unknown.</summary>
public sealed record RealtimeCapabilities
{
    /// <summary>Audio encodings accepted for input. Empty when none are reported.</summary>
    public IReadOnlySet<RealtimeAudioEncoding> InputEncodings { get; init => field = RealtimeData.Set(value); } = FrozenSet<RealtimeAudioEncoding>.Empty;
    /// <summary>Audio encodings available for output. Empty when none are reported.</summary>
    public IReadOnlySet<RealtimeAudioEncoding> OutputEncodings { get; init => field = RealtimeData.Set(value); } = FrozenSet<RealtimeAudioEncoding>.Empty;
    /// <summary>Kinds of provider-hosted tools the session accepts. Empty when none are reported.</summary>
    public IReadOnlySet<LlmHostedToolKind> SupportedHostedToolKinds { get; init => field = RealtimeData.Set(value); } = FrozenSet<LlmHostedToolKind>.Empty;
    /// <summary>Input sample rates in hertz, or null when not reported.</summary>
    public IReadOnlyList<int>? InputSampleRates { get; init => field = value is null ? null : RealtimeData.Copy(value); }
    /// <summary>Output sample rates in hertz, or null when not reported.</summary>
    public IReadOnlyList<int>? OutputSampleRates { get; init => field = value is null ? null : RealtimeData.Copy(value); }
    /// <summary>Support for <see cref="RealtimeManualTurnDetection"/>.</summary>
    public InferenceSupport SupportsManualTurns { get; init; }
    /// <summary>Support for <see cref="RealtimeServerVad"/>.</summary>
    public InferenceSupport SupportsServerVad { get; init; }
    /// <summary>Support for client-executed function tools.</summary>
    public InferenceSupport SupportsClientTools { get; init; }
    /// <summary>Support for structured tool output beyond plain text.</summary>
    public InferenceSupport SupportsStructuredToolOutput { get; init; }
    /// <summary>Support for <see cref="RealtimeResumptionOptions"/>.</summary>
    public InferenceSupport SupportsResumption { get; init; }
    /// <summary>Support for <see cref="RealtimeAudioTransport.Binary"/> audio transport.</summary>
    public InferenceSupport SupportsBinaryTransport { get; init; }
    /// <summary>Support for pronunciation replacement in spoken output.</summary>
    public InferenceSupport SupportsPronunciationReplacement { get; init; }
    /// <summary>Support for forced messages.</summary>
    public InferenceSupport SupportsForcedMessages { get; init; }
}

/// <summary>One explicit session owner. Command completion means ordered send, not event consumption.</summary>
public interface IRealtimeVoiceSession : IAsyncDisposable
{
    /// <summary>Sends new session settings.</summary>
    Task UpdateSessionAsync(RealtimeSessionConfig config, CancellationToken cancellationToken = default);
    /// <summary>Appends encoded audio to the input buffer.</summary>
    Task AppendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken = default);
    /// <summary>Commits the input buffer as a user audio item.</summary>
    Task CommitAudioAsync(CancellationToken cancellationToken = default);
    /// <summary>Discards the uncommitted input buffer.</summary>
    Task ClearAudioAsync(CancellationToken cancellationToken = default);
    /// <summary>Adds an item to the conversation after <paramref name="previousItemId"/>, or at the end when it is null.</summary>
    Task AddItemAsync(RealtimeConversationItem item, string? previousItemId = null, CancellationToken cancellationToken = default);
    /// <summary>Removes an item from the conversation.</summary>
    Task DeleteItemAsync(string itemId, CancellationToken cancellationToken = default);
    /// <summary>Cuts the audio of one content part of an item at <paramref name="audioEnd"/>, for example after the user interrupts playback.</summary>
    Task TruncateItemAsync(string itemId, int contentIndex, TimeSpan audioEnd, CancellationToken cancellationToken = default);
    /// <summary>Requests a model response, with optional per-response settings.</summary>
    Task CreateResponseAsync(RealtimeResponseOptions? options = null, CancellationToken cancellationToken = default);
    /// <summary>Cancels the named response, or the active response when <paramref name="responseId"/> is null.</summary>
    Task CancelResponseAsync(string? responseId = null, CancellationToken cancellationToken = default);
    /// <summary>Sends the result of a client function call.</summary>
    Task SubmitToolResultAsync(RealtimeToolResult result, CancellationToken cancellationToken = default);
    /// <summary>Events the session receives from the provider, in arrival order.</summary>
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
