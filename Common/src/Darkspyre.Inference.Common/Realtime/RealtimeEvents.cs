using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;

namespace Darkspyre.Inference.Common.Realtime;

public sealed record RealtimeEventIdentity(string? EventId = null, string? ResponseId = null,
    string? ItemId = null, int? OutputIndex = null, int? ContentIndex = null)
{
    public override string ToString() => "RealtimeEventIdentity { <identity> }";
}

public abstract record RealtimeEvent(RealtimeEventIdentity Identity)
{
    [JsonRequired] public RealtimeEventIdentity Identity { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Identity);
}

public sealed record RealtimeSessionEvent(RealtimeEventIdentity Identity,
    string Stage, string? SessionId, RealtimeSessionConfig? Configuration) : RealtimeEvent(Identity)
{
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Stage);
    public override string ToString() => "RealtimeSessionEvent { <event> }";
}
public sealed record RealtimeConversationCreatedEvent(RealtimeEventIdentity Identity,
    RealtimeConversationReference? Conversation) : RealtimeEvent(Identity)
{
    public override string ToString() => "RealtimeConversationCreatedEvent { <event> }";
}
public sealed record RealtimeInputSpeechEvent(RealtimeEventIdentity Identity,
    string Stage, TimeSpan? Start, TimeSpan? End) : RealtimeEvent(Identity)
{
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Stage);
    public override string ToString() => "RealtimeInputSpeechEvent { <event> }";
}
public sealed record RealtimeInputBufferEvent(RealtimeEventIdentity Identity,
    string Stage, string? PreviousItemId) : RealtimeEvent(Identity)
{
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Stage);
    public override string ToString() => "RealtimeInputBufferEvent { <event> }";
}
public sealed record RealtimeItemEvent(RealtimeEventIdentity Identity, string Stage,
    RealtimeConversationItem? Item, string? PreviousItemId,
    TimeSpan? AudioEnd = null, string? Transcript = null) : RealtimeEvent(Identity)
{
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Stage);
    public override string ToString() => "RealtimeItemEvent { <event> }";
}
public sealed record RealtimeContentPartEvent(RealtimeEventIdentity Identity,
    string Stage, RealtimeContent? Part) : RealtimeEvent(Identity)
{
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Stage);
    public override string ToString() => "RealtimeContentPartEvent { <event> }";
}
public sealed record RealtimeTranscriptEvent(RealtimeEventIdentity Identity, string Text,
    [property: JsonRequired] bool IsInput, [property: JsonRequired] bool IsSnapshot,
    [property: JsonRequired] bool IsFinal) : RealtimeEvent(Identity)
{
    [JsonRequired] public string Text { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Text);
    public override string ToString() => "RealtimeTranscriptEvent { <event> }";
}
public sealed record RealtimeAudioDeltaEvent(RealtimeEventIdentity Identity, ReadOnlyMemory<byte> Audio) : RealtimeEvent(Identity)
{
    [JsonRequired] public ReadOnlyMemory<byte> Audio { get; init => field = value.ToArray(); } = Audio.ToArray();
    public override string ToString() => "RealtimeAudioDeltaEvent { <event> }";
}
public sealed record RealtimeAudioDoneEvent(RealtimeEventIdentity Identity) : RealtimeEvent(Identity)
{
    public override string ToString() => "RealtimeAudioDoneEvent { <event> }";
}
public sealed record RealtimeTextDeltaEvent(RealtimeEventIdentity Identity, string Text) : RealtimeEvent(Identity)
{
    [JsonRequired] public string Text { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Text);
    public override string ToString() => "RealtimeTextDeltaEvent { <event> }";
}
public sealed record RealtimeFunctionArgumentsEvent(RealtimeEventIdentity Identity,
    string? CallId, string? Name, string Arguments, [property: JsonRequired] bool IsFinal) : RealtimeEvent(Identity)
{
    [JsonRequired] public string Arguments { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Arguments);
    public override string ToString() => "RealtimeFunctionArgumentsEvent { <event> }";
}
public sealed record RealtimeHostedStartedEvent(RealtimeEventIdentity Identity, LlmHostedToolCallStartedEvent Call) : RealtimeEvent(Identity)
{
    [JsonRequired] public LlmHostedToolCallStartedEvent Call { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Call);
    public override string ToString() => "RealtimeHostedStartedEvent { <event> }";
}
public sealed record RealtimeHostedProgressEvent(RealtimeEventIdentity Identity, LlmHostedToolCallProgressEvent Call) : RealtimeEvent(Identity)
{
    [JsonRequired] public LlmHostedToolCallProgressEvent Call { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Call);
    public override string ToString() => "RealtimeHostedProgressEvent { <event> }";
}
public sealed record RealtimeHostedCompletedEvent(RealtimeEventIdentity Identity, LlmHostedToolCall Call) : RealtimeEvent(Identity)
{
    [JsonRequired] public LlmHostedToolCall Call { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Call);
    public override string ToString() => "RealtimeHostedCompletedEvent { <event> }";
}
public sealed record RealtimeResponseEvent(RealtimeEventIdentity Identity, string Stage,
    LlmCompletionStatus? Status, LlmUsage? Usage, IReadOnlyDictionary<string, string>? Metadata) : RealtimeEvent(Identity)
{
    [JsonRequired] public string Stage { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Stage);
    public IReadOnlyDictionary<string, string>? Metadata { get; init => field = RealtimeData.Map(value); } = RealtimeData.Map(Metadata);
    public override string ToString() => "RealtimeResponseEvent { <event> }";
}
public sealed record RealtimeErrorEvent(RealtimeEventIdentity Identity, InferenceResourceFailure Error,
    string? Parameter = null, string? RelatedClientEventId = null) : RealtimeEvent(Identity)
{
    [JsonRequired] public InferenceResourceFailure Error { get; init => field = RealtimeData.Required(value); } = RealtimeData.Required(Error);
    public override string ToString() => "RealtimeErrorEvent { <event> }";
}
