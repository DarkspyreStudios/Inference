namespace Darkspyre.Inference.Connect.LLM;

public enum LlmOperationSupport
{
    Supported,
    Unsupported,
    Unknown
}

public enum LlmAgentResourceState
{
    Pending,
    Running,
    Paused,
    Completed,
    Failed,
    Stopped,
    Unknown
}

public sealed record LlmAgentResourceSupport(
    LlmOperationSupport Output,
    LlmOperationSupport Stop,
    LlmOperationSupport Terminal,
    string? Diagnostic = null);

public sealed record LlmAgentResourceUsage(long TotalTokens, int ToolUses, long DurationMs);

/// <summary>Background work inside the runtime's own process. ResourceId is the runtime's opaque identity.</summary>
public sealed record LlmAgentResource(string ResourceId, string Kind, string Label, LlmAgentResourceState State)
{
    public DateTimeOffset? Started { get; init; }

    public DateTimeOffset? Ended { get; init; }

    public int? ExitCode { get; init; }

    public string? Error { get; init; }

    /// <summary>The tool call that started it, when the runtime reports one.</summary>
    public string? OriginToolCallId { get; init; }

    /// <summary>The resource that owns it, such as the subagent that ran a command.</summary>
    public string? ParentResourceId { get; init; }

    public string? AgentType { get; init; }

    public bool Backgrounded { get; init; }

    public LlmAgentResourceUsage? Usage { get; init; }

    public string? Summary { get; init; }

    public required LlmAgentResourceSupport Support { get; init; }
}

public enum LlmAgentResourceChange
{
    Added,
    Updated,
    Ended
}

/// <summary>A resource was added, changed or ended. Carried on the session feed, outside any turn.</summary>
public sealed record LlmAgentResourceEvent(string ThreadId, LlmAgentResourceChange Change, LlmAgentResource Resource)
    : LlmAgentEvent(ThreadId, string.Empty);

/// <summary>New output is readable up to LatestCursor. A hint, not the data.</summary>
public sealed record LlmAgentResourceOutputEvent(string ThreadId, string ResourceId, string LatestCursor)
    : LlmAgentEvent(ThreadId, string.Empty);

/// <summary>
/// The runtime started a turn without host input. ResourceId names the resource whose end or report
/// immediately preceded it, or is null when the runtime gives no such signal.
/// </summary>
public sealed record LlmAgentProviderTurnStartedEvent(string ThreadId, string TurnId, string? ResourceId)
    : LlmAgentEvent(ThreadId, TurnId);

public enum LlmAgentResourceOutputStatus
{
    Data,
    NoData,
    Gap,
    Unavailable
}

/// <summary>
/// One nondestructive read. NextCursor continues after Data; EarliestCursor is the oldest retained position.
/// Closed means no further output will arrive.
/// </summary>
public sealed record LlmAgentResourceOutput(
    LlmAgentResourceOutputStatus Status,
    ReadOnlyMemory<byte> Data,
    string? NextCursor,
    string? EarliestCursor,
    bool Closed);

public enum LlmAgentResourceStopStatus
{
    Requested,
    Exited,
    Unsupported,
    Failed,
    Unknown
}
