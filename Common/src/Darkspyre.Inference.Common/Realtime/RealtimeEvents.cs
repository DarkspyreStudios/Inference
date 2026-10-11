using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.Realtime;

/// <summary>Response audio committed to a caller-owned resource during a realtime operation.</summary>
/// <param name="Identity">The provider identity of the output the audio came from.</param>
/// <param name="Resource">The resource that holds the committed audio.</param>
/// <param name="Audio">The audio format of the committed bytes, or null when unknown.</param>
public sealed record RealtimeAudioArtifact(RealtimeEventIdentity Identity,
    InferenceResourceDescriptor Resource, RealtimeAudioConfig? Audio = null)
{
    /// <summary>The provider identity of the output the audio came from.</summary>
    [JsonRequired] public RealtimeEventIdentity Identity { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Identity);
    /// <summary>The resource that holds the committed audio.</summary>
    [JsonRequired] public InferenceResourceDescriptor Resource { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Resource);
    /// <summary>The resource selection version after the commit, or null when none was recorded.</summary>
    public InferenceResourceSelectionVersion? SelectionVersion { get; init; }
    /// <summary>Returns the type name only, so logs never carry the artifact's data.</summary>
    public override string ToString() => "RealtimeAudioArtifact { <data> }";
}

/// <summary>The provider identifiers a realtime event carries. Each is null when the provider omits it.</summary>
/// <param name="EventId">The provider's id for the event.</param>
/// <param name="ResponseId">The response the event belongs to.</param>
/// <param name="ItemId">The conversation item the event belongs to.</param>
/// <param name="OutputIndex">The position of the output item within the response.</param>
/// <param name="ContentIndex">The position of the content part within the item.</param>
public sealed record RealtimeEventIdentity(string? EventId = null, string? ResponseId = null,
    string? ItemId = null, int? OutputIndex = null, int? ContentIndex = null)
{
    /// <summary>Returns the type name only, so logs never carry provider identifiers.</summary>
    public override string ToString() => "RealtimeEventIdentity { <identity> }";
}

/// <summary>An event a realtime session receives from the provider.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
public abstract record RealtimeEvent(RealtimeEventIdentity Identity)
{
    /// <summary>The provider identifiers of the event.</summary>
    [JsonRequired] public RealtimeEventIdentity Identity { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Identity);
}

/// <summary>Reports that the provider created or updated the realtime session.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Stage">The provider event type, such as <c>session.created</c>.</param>
/// <param name="SessionId">The provider's session id, or null when omitted.</param>
/// <param name="Configuration">The session configuration the provider reports, or null when omitted.</param>
public sealed record RealtimeSessionEvent(RealtimeEventIdentity Identity,
    string Stage, string? SessionId, RealtimeSessionConfig? Configuration) : RealtimeEvent(Identity)
{
    /// <summary>The provider event type, such as <c>session.created</c>.</summary>
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Stage);
    /// <summary>The output modalities the session uses, or null when the provider omits them.</summary>
    public IReadOnlyList<RealtimeModality>? Modalities { get; init => field = value is null ? null : RealtimeData.Copy(value.Select(RealtimeData.Defined).ToArray()); }
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeSessionEvent { <event> }";
}
/// <summary>Reports that the provider created a conversation.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Conversation">The created conversation, or null when the provider omits it.</param>
public sealed record RealtimeConversationCreatedEvent(RealtimeEventIdentity Identity,
    RealtimeConversationReference? Conversation) : RealtimeEvent(Identity)
{
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeConversationCreatedEvent { <event> }";
}
/// <summary>Reports that the provider detected the start or end of speech in the input audio.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Stage">The provider event type, such as <c>input_audio_buffer.speech_started</c>.</param>
/// <param name="Start">The offset of the speech start in the input audio, or null when omitted.</param>
/// <param name="End">The offset of the speech end in the input audio, or null when omitted.</param>
public sealed record RealtimeInputSpeechEvent(RealtimeEventIdentity Identity,
    string Stage, TimeSpan? Start, TimeSpan? End) : RealtimeEvent(Identity)
{
    /// <summary>The provider event type, such as <c>input_audio_buffer.speech_started</c>.</summary>
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Stage);
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeInputSpeechEvent { <event> }";
}
/// <summary>Reports a change to the input audio buffer: committed, cleared or timed out.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Stage">The provider event type, such as <c>input_audio_buffer.committed</c>.</param>
/// <param name="PreviousItemId">The conversation item the committed input follows, or null when omitted.</param>
public sealed record RealtimeInputBufferEvent(RealtimeEventIdentity Identity,
    string Stage, string? PreviousItemId) : RealtimeEvent(Identity)
{
    /// <summary>The provider event type, such as <c>input_audio_buffer.committed</c>.</summary>
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Stage);
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeInputBufferEvent { <event> }";
}
/// <summary>Reports that a conversation item was added, completed, deleted or truncated.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Stage">The provider event type, such as <c>conversation.item.added</c>.</param>
/// <param name="Item">The item, or null for a deletion or truncation.</param>
/// <param name="PreviousItemId">The item this item follows, or null when omitted.</param>
/// <param name="AudioEnd">The audio offset a truncation cut the item at, or null.</param>
/// <param name="Transcript">The transcript that remains after a truncation, or null.</param>
public sealed record RealtimeItemEvent(RealtimeEventIdentity Identity, string Stage,
    RealtimeConversationItem? Item, string? PreviousItemId,
    TimeSpan? AudioEnd = null, string? Transcript = null) : RealtimeEvent(Identity)
{
    /// <summary>The provider event type, such as <c>conversation.item.added</c>.</summary>
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Stage);
    /// <summary>The item status as the provider reports it, or null when omitted.</summary>
    public string? ProviderStatus { get; init; }
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeItemEvent { <event> }";
}
/// <summary>Reports that a content part of a response item was added or completed.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Stage">The provider event type, such as <c>response.content_part.added</c>.</param>
/// <param name="Part">The content part, or null when omitted.</param>
public sealed record RealtimeContentPartEvent(RealtimeEventIdentity Identity,
    string Stage, RealtimeContent? Part) : RealtimeEvent(Identity)
{
    /// <summary>The provider event type, such as <c>response.content_part.added</c>.</summary>
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Stage);
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeContentPartEvent { <event> }";
}
/// <summary>Carries transcript text for input audio or response audio.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Text">The transcript text: a delta, or the whole transcript when a snapshot.</param>
/// <param name="IsInput">True for a transcript of input audio; false for response audio.</param>
/// <param name="IsSnapshot">True when <paramref name="Text"/> holds the whole transcript so far rather than a delta.</param>
/// <param name="IsFinal">True when the transcript is complete.</param>
public sealed record RealtimeTranscriptEvent(RealtimeEventIdentity Identity, string Text,
    [property: JsonRequired] bool IsInput, [property: JsonRequired] bool IsSnapshot,
    [property: JsonRequired] bool IsFinal) : RealtimeEvent(Identity)
{
    /// <summary>The transcript text: a delta, or the whole transcript when a snapshot.</summary>
    [JsonRequired] public string Text { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Text);
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeTranscriptEvent { <event> }";
}
/// <summary>Carries the next chunk of response audio.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Audio">The audio bytes. The event keeps its own copy.</param>
public sealed record RealtimeAudioDeltaEvent(RealtimeEventIdentity Identity, ReadOnlyMemory<byte> Audio) : RealtimeEvent(Identity)
{
    /// <summary>The audio bytes. The event keeps its own copy.</summary>
    [JsonRequired] public ReadOnlyMemory<byte> Audio { get; init => field = value.ToArray(); } = Audio.ToArray();
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeAudioDeltaEvent { <event> }";
}
/// <summary>Reports that the response audio for an output is complete.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
public sealed record RealtimeAudioDoneEvent(RealtimeEventIdentity Identity) : RealtimeEvent(Identity)
{
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeAudioDoneEvent { <event> }";
}
/// <summary>Carries the next chunk of response text.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Text">The text chunk.</param>
public sealed record RealtimeTextDeltaEvent(RealtimeEventIdentity Identity, string Text) : RealtimeEvent(Identity)
{
    /// <summary>The text chunk.</summary>
    [JsonRequired] public string Text { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Text);
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeTextDeltaEvent { <event> }";
}
/// <summary>Carries function call arguments the model streams for a caller-defined tool.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="CallId">The function call id, or null when omitted.</param>
/// <param name="Name">The function name, or null when omitted.</param>
/// <param name="Arguments">An arguments delta, or the complete arguments when final.</param>
/// <param name="IsFinal">True when <paramref name="Arguments"/> holds the complete arguments.</param>
public sealed record RealtimeFunctionArgumentsEvent(RealtimeEventIdentity Identity,
    string? CallId, string? Name, string Arguments, [property: JsonRequired] bool IsFinal) : RealtimeEvent(Identity)
{
    /// <summary>An arguments delta, or the complete arguments when final.</summary>
    [JsonRequired] public string Arguments { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Arguments);
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeFunctionArgumentsEvent { <event> }";
}
/// <summary>Reports that a provider-hosted tool call started.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Call">The started call.</param>
public sealed record RealtimeHostedStartedEvent(RealtimeEventIdentity Identity, LlmHostedToolCallStartedEvent Call) : RealtimeEvent(Identity)
{
    /// <summary>The started call.</summary>
    [JsonRequired] public LlmHostedToolCallStartedEvent Call { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Call);
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeHostedStartedEvent { <event> }";
}
/// <summary>Reports progress on a provider-hosted tool call.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Call">The call's progress.</param>
public sealed record RealtimeHostedProgressEvent(RealtimeEventIdentity Identity, LlmHostedToolCallProgressEvent Call) : RealtimeEvent(Identity)
{
    /// <summary>The call's progress.</summary>
    [JsonRequired] public LlmHostedToolCallProgressEvent Call { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Call);
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeHostedProgressEvent { <event> }";
}
/// <summary>Reports that a provider-hosted tool call completed.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Call">The completed call.</param>
public sealed record RealtimeHostedCompletedEvent(RealtimeEventIdentity Identity, LlmHostedToolCall Call) : RealtimeEvent(Identity)
{
    /// <summary>The completed call.</summary>
    [JsonRequired] public LlmHostedToolCall Call { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Call);
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeHostedCompletedEvent { <event> }";
}
/// <summary>A raw provider observation of hosted tool work, such as an MCP tool listing or call.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Stage">The provider event type, such as <c>response.mcp_call.in_progress</c>.</param>
/// <param name="Kind">The kind of hosted tool.</param>
/// <param name="CallId">The hosted call id, or null when omitted.</param>
/// <param name="Name">The hosted tool name, or null when omitted.</param>
/// <param name="Arguments">An arguments delta, or the complete arguments when final; null when the event carries none.</param>
/// <param name="IsArgumentsFinal">True when <paramref name="Arguments"/> is complete; null when the event carries no arguments.</param>
/// <param name="Error">The failure the provider reported, or null.</param>
public sealed record RealtimeHostedObservationEvent(RealtimeEventIdentity Identity,
    string Stage, LlmHostedToolKind Kind, string? CallId = null, string? Name = null,
    string? Arguments = null, bool? IsArgumentsFinal = null, InferenceResourceFailure? Error = null) : RealtimeEvent(Identity)
{
    /// <summary>The provider event type, such as <c>response.mcp_call.in_progress</c>.</summary>
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Identity(value); } = RealtimeData.Identity(Stage);
    /// <summary>The kind of hosted tool.</summary>
    [JsonRequired] public LlmHostedToolKind Kind { get; init => field = RealtimeData.Defined(value); } = RealtimeData.Defined(Kind);
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeHostedObservationEvent { <event> }";
}
/// <summary>Reports that a response was created or finished.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Stage">The provider event type, such as <c>response.done</c>.</param>
/// <param name="Status">The completion status, or null when the provider status has no mapping.</param>
/// <param name="Usage">The token usage, or null when omitted.</param>
/// <param name="Metadata">The response metadata, or null when omitted.</param>
public sealed record RealtimeResponseEvent(RealtimeEventIdentity Identity, string Stage,
    LlmCompletionStatus? Status, LlmUsage? Usage, IReadOnlyDictionary<string, string>? Metadata) : RealtimeEvent(Identity)
{
    /// <summary>The provider event type, such as <c>response.done</c>.</summary>
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Stage);
    /// <summary>The response metadata, or null when omitted. The event keeps its own copy.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init => field = RealtimeData.Map(value); } = RealtimeData.Map(Metadata);
    /// <summary>The response audio committed to caller-owned resources, or null when the session stores no audio.</summary>
    public IReadOnlyList<RealtimeAudioArtifact>? AudioArtifacts { get; init => field = value is null ? null : RealtimeData.Copy(value); }
    /// <summary>The response status as the provider reports it, or null when omitted.</summary>
    public string? ProviderStatus { get; init; }
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeResponseEvent { <event> }";
}
/// <summary>Reports a failure from the provider or from the realtime operation.</summary>
/// <param name="Identity">The provider identifiers of the event.</param>
/// <param name="Error">The failure.</param>
/// <param name="Parameter">The request parameter the failure concerns, or null.</param>
/// <param name="RelatedClientEventId">The client event that caused the failure, or null.</param>
public sealed record RealtimeErrorEvent(RealtimeEventIdentity Identity, InferenceResourceFailure Error,
    string? Parameter = null, string? RelatedClientEventId = null) : RealtimeEvent(Identity)
{
    /// <summary>The failure.</summary>
    [JsonRequired] public InferenceResourceFailure Error { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Error);
    /// <summary>Response audio already committed to caller-owned resources before the failure, or null.</summary>
    public IReadOnlyList<RealtimeAudioArtifact>? AudioArtifacts { get; init => field = value is null ? null : RealtimeData.Copy(value); }
    /// <summary>The response status as the provider last reported it, or null.</summary>
    public string? ProviderStatus { get; init; }
    /// <summary>Returns the type name only, so logs never carry event data.</summary>
    public override string ToString() => "RealtimeErrorEvent { <event> }";
}
