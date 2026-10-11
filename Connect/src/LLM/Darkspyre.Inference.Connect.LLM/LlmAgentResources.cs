using System.Diagnostics.CodeAnalysis;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Whether a runtime supports an operation.</summary>
public enum LlmOperationSupport
{
    /// <summary>The operation is supported.</summary>
    Supported,
    /// <summary>The operation is not supported.</summary>
    Unsupported,
    /// <summary>Support cannot be determined.</summary>
    Unknown
}

/// <summary>The lifecycle state the runtime reports for a resource.</summary>
public enum LlmAgentResourceState
{
    /// <summary>The resource is queued or has not started.</summary>
    Pending,
    /// <summary>The resource is running.</summary>
    Running,
    /// <summary>The resource is paused.</summary>
    Paused,
    /// <summary>The resource ended successfully.</summary>
    Completed,
    /// <summary>The resource ended with a failure.</summary>
    Failed,
    /// <summary>The resource was stopped.</summary>
    Stopped,
    /// <summary>The state cannot be determined.</summary>
    Unknown
}

/// <summary>The operations the runtime supports on one resource.</summary>
/// <param name="Output">Whether the resource's output can be read.</param>
/// <param name="Stop">Whether the resource can be stopped.</param>
/// <param name="Terminal">Whether a terminal can be attached to the resource.</param>
/// <param name="Diagnostic">An explanation of the support values, or null.</param>
public sealed record LlmAgentResourceSupport(
    LlmOperationSupport Output,
    LlmOperationSupport Stop,
    LlmOperationSupport Terminal,
    string? Diagnostic = null);

/// <summary>The usage the runtime reports for one resource.</summary>
/// <param name="TotalTokens">The tokens the resource used.</param>
/// <param name="ToolUses">The number of tool calls the resource made.</param>
/// <param name="DurationMs">The resource's running time in milliseconds.</param>
public sealed record LlmAgentResourceUsage(long TotalTokens, int ToolUses, long DurationMs);

/// <summary>Background work inside the runtime's own process. ResourceId is the runtime's opaque identity.</summary>
/// <param name="ResourceId">The runtime's opaque identity for the resource.</param>
/// <param name="Kind">The runtime's name for the kind of resource.</param>
/// <param name="Label">The text that names the resource for display.</param>
/// <param name="State">The lifecycle state the runtime reports.</param>
public sealed record LlmAgentResource(string ResourceId, string Kind, string Label, LlmAgentResourceState State)
{
    /// <summary>When the resource started, or null when the runtime does not report it.</summary>
    public DateTimeOffset? Started { get; init; }

    /// <summary>When the resource ended, or null while it runs or when the runtime does not report it.</summary>
    public DateTimeOffset? Ended { get; init; }

    /// <summary>The resource's exit code, when it has one.</summary>
    public int? ExitCode { get; init; }

    /// <summary>The runtime's error message for a failed resource, or null.</summary>
    public string? Error { get; init; }

    /// <summary>The tool call that started it, when the runtime reports one.</summary>
    public string? OriginToolCallId { get; init; }

    /// <summary>The resource that owns it, such as the subagent that ran a command.</summary>
    public string? ParentResourceId { get; init; }

    /// <summary>The runtime's agent type for a subagent resource, or null.</summary>
    public string? AgentType { get; init; }

    /// <summary>True when the runtime reports the resource as running in the background.</summary>
    public bool Backgrounded { get; init; }

    /// <summary>The resource's token, tool and time usage, or null when the runtime does not report it.</summary>
    public LlmAgentResourceUsage? Usage { get; init; }

    /// <summary>The runtime's short summary of the resource's work or result, or null.</summary>
    public string? Summary { get; init; }

    /// <summary>The operations the runtime supports on this resource.</summary>
    public required LlmAgentResourceSupport Support { get; init; }
}

/// <summary>The kind of change a resource event reports.</summary>
public enum LlmAgentResourceChange
{
    /// <summary>The runtime started tracking the resource.</summary>
    Added,
    /// <summary>The resource's reported fields changed.</summary>
    Updated,
    /// <summary>The resource ended.</summary>
    Ended
}

/// <summary>A resource was added, changed or ended. Carried on the session feed, outside any turn.</summary>
/// <param name="Change">What happened to the resource.</param>
/// <param name="Resource">The resource as reported after the change.</param>
/// <param name="ThreadId">The thread the event belongs to.</param>
public sealed record LlmAgentResourceEvent(string ThreadId, LlmAgentResourceChange Change, LlmAgentResource Resource)
    : LlmAgentEvent(ThreadId, null);

/// <summary>New output is readable up to LatestCursor. A hint, not the data.</summary>
/// <param name="ResourceId">The resource that produced the output.</param>
/// <param name="LatestCursor">The position up to which output is readable.</param>
/// <param name="ThreadId">The thread the event belongs to.</param>
public sealed record LlmAgentResourceOutputEvent(string ThreadId, string ResourceId, string LatestCursor)
    : LlmAgentEvent(ThreadId, null);

/// <summary>Coalescible hint for committed selected-resource membership, not a per-resource feed.</summary>
/// <param name="ResourceSelectionRevision">The revision of the selected-resource membership after the change.</param>
/// <param name="ThreadId">The thread the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to, or null outside a turn.</param>
public sealed record LlmAgentResourceSelectionChangedEvent(string ThreadId, string? TurnId,
    [property: System.Text.Json.Serialization.JsonRequired] long ResourceSelectionRevision) : LlmAgentEvent(HostedEventFacts.Identity(ThreadId), TurnId is null ? null : HostedEventFacts.Identity(TurnId))
{
    /// <summary>The identity of the thread whose resource selection changed.</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public override string ThreadId { get => base.ThreadId; init => base.ThreadId = HostedEventFacts.Identity(value); }
    /// <summary>The identity of the turn that committed the change, or null when the change happened outside a turn.</summary>
    public override string? TurnId { get => base.TurnId; init => base.TurnId = value is null ? null : HostedEventFacts.Identity(value); }
}

/// <summary>
/// The runtime started a turn without host input. ResourceId names the resource whose end or report
/// immediately preceded it, or is null when the runtime gives no such signal.
/// </summary>
/// <param name="ResourceId">The resource whose end or report preceded the turn, or null when the runtime gives no such signal.</param>
/// <param name="ThreadId">The thread the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to, or null outside a turn.</param>
public sealed record LlmAgentProviderTurnStartedEvent(string ThreadId, string TurnId, string? ResourceId)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The identity of the turn the runtime started.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>The outcome of one resource output read.</summary>
public enum LlmAgentResourceOutputStatus
{
    /// <summary>The read returned output at the cursor.</summary>
    Data,
    /// <summary>No output exists at the cursor yet.</summary>
    NoData,
    /// <summary>Output at the cursor is no longer retained. The next cursor points at retained output.</summary>
    Gap,
    /// <summary>The runtime does not retain the resource's output.</summary>
    Unavailable
}

/// <summary>
/// One nondestructive read. NextCursor continues after Data; EarliestCursor is the oldest retained position.
/// Closed means no further output will arrive.
/// </summary>
/// <param name="Status">The outcome of the read.</param>
/// <param name="Data">The output bytes read.</param>
/// <param name="NextCursor">The position after the bytes read, or null when there is none.</param>
/// <param name="EarliestCursor">The oldest retained position, or null when there is none.</param>
/// <param name="Closed">True when no further output will arrive.</param>
public sealed record LlmAgentResourceOutput(
    LlmAgentResourceOutputStatus Status,
    ReadOnlyMemory<byte> Data,
    string? NextCursor,
    string? EarliestCursor,
    bool Closed);

/// <summary>The outcome of a request to stop a runtime resource.</summary>
public enum LlmAgentResourceStopStatus
{
    /// <summary>The stop was requested and the resource has not exited yet.</summary>
    Requested,
    /// <summary>The resource has exited.</summary>
    Exited,
    /// <summary>The runtime cannot stop the resource.</summary>
    Unsupported,
    /// <summary>The runtime did not accept the stop request.</summary>
    Failed,
    /// <summary>The runtime does not know the resource, or the outcome cannot be determined.</summary>
    Unknown
}
