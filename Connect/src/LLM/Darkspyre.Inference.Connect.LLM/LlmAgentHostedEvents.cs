using Darkspyre.Inference.Common.LLM;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>A provider-hosted tool call began.</summary>
/// <param name="ThreadId">The conversation the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to.</param>
/// <param name="RequestId">The model request within the turn that produced the event.</param>
/// <param name="CallId">The hosted tool call identity.</param>
/// <param name="Kind">The kind of hosted tool.</param>
/// <param name="Name">The hosted tool name.</param>
public sealed record LlmAgentHostedToolCallStartedEvent(string ThreadId, string TurnId, string RequestId,
    string CallId, LlmHostedToolKind Kind,
    string Name) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    /// <summary>The model request within the turn that produced the event.</summary>
    [JsonRequired]
    public string RequestId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Scope(ThreadId, TurnId, RequestId);
    /// <summary>The hosted tool call identity.</summary>
    [JsonRequired]
    public string CallId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Identity(CallId);
    /// <summary>The kind of hosted tool.</summary>
    [JsonRequired]
    public LlmHostedToolKind Kind { get; init => field = HostedEventFacts.Defined(value); } = HostedEventFacts.Defined(Kind);
    /// <summary>The hosted tool name.</summary>
    [JsonRequired]
    public string Name { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Identity(Name);
}
/// <summary>A provider-hosted tool call moved to a new phase.</summary>
/// <param name="ThreadId">The conversation the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to.</param>
/// <param name="RequestId">The model request within the turn that produced the event.</param>
/// <param name="CallId">The hosted tool call identity.</param>
/// <param name="Phase">The phase the call reached.</param>
public sealed record LlmAgentHostedToolCallProgressEvent(string ThreadId, string TurnId, string RequestId,
    string CallId, LlmHostedToolPhase Phase) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    /// <summary>The model request within the turn that produced the event.</summary>
    [JsonRequired]
    public string RequestId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Scope(ThreadId, TurnId, RequestId);
    /// <summary>The hosted tool call identity.</summary>
    [JsonRequired]
    public string CallId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Identity(CallId);
    /// <summary>The phase the call reached.</summary>
    [JsonRequired]
    public LlmHostedToolPhase Phase { get; init => field = HostedEventFacts.Defined(value); } = HostedEventFacts.Defined(Phase);
}
/// <summary>A provider-hosted tool call finished.</summary>
/// <param name="ThreadId">The conversation the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to.</param>
/// <param name="RequestId">The model request within the turn that produced the event.</param>
/// <param name="Call">The finished call and its output.</param>
public sealed record LlmAgentHostedToolCallCompletedEvent(string ThreadId, string TurnId, string RequestId,
    LlmHostedToolCall Call) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    /// <summary>The model request within the turn that produced the event.</summary>
    [JsonRequired]
    public string RequestId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Scope(ThreadId, TurnId, RequestId);
    /// <summary>The finished call and its output.</summary>
    [JsonRequired]
    public LlmHostedToolCall Call { get; init => field = HostedEventFacts.Required(value); } = HostedEventFacts.Required(Call);
}
/// <summary>A citation attached to the answer.</summary>
/// <param name="ThreadId">The conversation the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to.</param>
/// <param name="RequestId">The model request within the turn that produced the event.</param>
/// <param name="Citation">The citation.</param>
public sealed record LlmAgentCitationEvent(string ThreadId, string TurnId, string RequestId,
    LlmCitation Citation) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    /// <summary>The model request within the turn that produced the event.</summary>
    [JsonRequired]
    public string RequestId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Scope(ThreadId, TurnId, RequestId);
    /// <summary>The citation.</summary>
    [JsonRequired]
    public LlmCitation Citation { get; init => field = HostedEventFacts.Required(value); } = HostedEventFacts.Required(Citation);
}
/// <summary>The terminal result's ordered citation collection, not an append or clearing instruction.</summary>
public sealed record LlmAgentCitationsSnapshotEvent(string ThreadId, string TurnId, string RequestId,
    IReadOnlyList<LlmCitation> Citations) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    /// <summary>The model request within the turn that produced the event.</summary>
    [JsonRequired]
    public string RequestId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Scope(ThreadId, TurnId, RequestId);
    /// <summary>The ordered citations of the terminal result.</summary>
    [JsonRequired]
    public IReadOnlyList<LlmCitation> Citations { get; init => field = CopyCitations(value); } = CopyCitations(Citations);

    private static ReadOnlyCollection<LlmCitation> CopyCitations(IReadOnlyList<LlmCitation> citations)
    {
        ArgumentNullException.ThrowIfNull(citations);
        LlmCitation[] copy = citations.ToArray();
        foreach (LlmCitation citation in copy) ArgumentNullException.ThrowIfNull(citation);
        return Array.AsReadOnly(copy);
    }
}
/// <summary>The conversation context was compacted.</summary>
/// <param name="ThreadId">The conversation the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to.</param>
/// <param name="RequestId">The model request within the turn that produced the event.</param>
/// <param name="ContextRevision">The context revision after compaction.</param>
/// <param name="DroppedMessageCount">Messages compaction removed, when reported.</param>
/// <param name="Usage">Token usage of the compaction, when reported.</param>
public sealed record LlmAgentCompactedEvent(string ThreadId, string TurnId, string RequestId,
    [property: JsonRequired] long ContextRevision, int? DroppedMessageCount, LlmUsage? Usage) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    /// <summary>The model request within the turn that produced the event.</summary>
    [JsonRequired]
    public string RequestId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Scope(ThreadId, TurnId, RequestId);

    /// <summary>Tool results whose text compaction shortened.</summary>
    public int ShortenedToolOutputCount { get; init; }

    /// <summary>Tool results whose images compaction removed.</summary>
    public int RemovedImageCount { get; init; }
}

/// <summary>An image the model produced, saved as a session resource.</summary>
public sealed record LlmAgentImageOutputEvent(string ThreadId, string TurnId, string ItemId,
    LlmResourceAttachment Image, string MediaType) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

internal static class HostedEventFacts
{
    internal static string Scope(string thread, string turn, string request) { Identity(thread); Identity(turn); return Identity(request); }
    internal static string Identity(string? value) { ArgumentException.ThrowIfNullOrWhiteSpace(value); return value; }
    internal static T Required<T>(T? value) where T : class { ArgumentNullException.ThrowIfNull(value); return value; }
    internal static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
}
