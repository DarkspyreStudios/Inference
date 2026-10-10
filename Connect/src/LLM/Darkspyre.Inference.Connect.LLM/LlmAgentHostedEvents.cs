using Darkspyre.Inference.Common.LLM;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM;

public sealed record LlmAgentHostedToolCallStartedEvent(string ThreadId, string TurnId, string RequestId,
    string CallId, LlmHostedToolKind Kind,
    string Name) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    [JsonRequired]
    public string RequestId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Scope(ThreadId, TurnId, RequestId);
    [JsonRequired]
    public string CallId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Identity(CallId);
    [JsonRequired]
    public LlmHostedToolKind Kind { get; init => field = HostedEventFacts.Defined(value); } = HostedEventFacts.Defined(Kind);
    [JsonRequired]
    public string Name { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Identity(Name);
}
public sealed record LlmAgentHostedToolCallProgressEvent(string ThreadId, string TurnId, string RequestId,
    string CallId, LlmHostedToolPhase Phase) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    [JsonRequired]
    public string RequestId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Scope(ThreadId, TurnId, RequestId);
    [JsonRequired]
    public string CallId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Identity(CallId);
    [JsonRequired]
    public LlmHostedToolPhase Phase { get; init => field = HostedEventFacts.Defined(value); } = HostedEventFacts.Defined(Phase);
}
public sealed record LlmAgentHostedToolCallCompletedEvent(string ThreadId, string TurnId, string RequestId,
    LlmHostedToolCall Call) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    [JsonRequired]
    public string RequestId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Scope(ThreadId, TurnId, RequestId);
    [JsonRequired]
    public LlmHostedToolCall Call { get; init => field = HostedEventFacts.Required(value); } = HostedEventFacts.Required(Call);
}
public sealed record LlmAgentCitationEvent(string ThreadId, string TurnId, string RequestId,
    LlmCitation Citation) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    [JsonRequired]
    public string RequestId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Scope(ThreadId, TurnId, RequestId);
    [JsonRequired]
    public LlmCitation Citation { get; init => field = HostedEventFacts.Required(value); } = HostedEventFacts.Required(Citation);
}
/// <summary>The terminal result's ordered citation collection, not an append or clearing instruction.</summary>
public sealed record LlmAgentCitationsSnapshotEvent(string ThreadId, string TurnId, string RequestId,
    IReadOnlyList<LlmCitation> Citations) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    [JsonRequired]
    public string RequestId { get; init => field = HostedEventFacts.Identity(value); } = HostedEventFacts.Scope(ThreadId, TurnId, RequestId);
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
public sealed record LlmAgentCompactedEvent(string ThreadId, string TurnId, string RequestId,
    [property: JsonRequired] long ContextRevision, int? DroppedMessageCount, LlmUsage? Usage) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

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
