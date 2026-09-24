namespace Darkspyre.Inference.Connect.LLM;

/// <summary>One stable record in model context, distinct from a display transcript.</summary>
public sealed record LlmAgentContextEntry(string Id, string TurnId, LlmChatMessage Message);

/// <summary>Settled model context. Contains no executable handlers or durable run checkpoint.</summary>
public sealed record LlmAgentContext
{
    public int FormatVersion { get; init; } = 1;

    public long Revision { get; init; }

    public IReadOnlyList<LlmAgentContextEntry> Entries { get; init; } = [];
}

public sealed record LlmAgentContextCaptureOptions
{
    /// <summary>
    /// False exports omission descriptors instead of bytes, without changing live context.
    /// Use true for live compaction; the host chooses its separate persistence policy.
    /// </summary>
    public bool IncludeAttachmentData { get; init; }
}

/// <summary>
/// Metadata for unavailable bytes on the containing message. ToolCallId is null for prompt
/// attachments; otherwise it identifies the owning tool result. Index is zero-based within the
/// message's attachment list, or within the owning tool output's parts. This metadata is never
/// substitute model input.
/// </summary>
public sealed record LlmOmittedAttachment(
    LlmBinaryKind Kind,
    string FileName,
    int Index,
    string? ToolCallId = null);

/// <summary>
/// Opaque adapter-owned assistant output. Preserve unchanged with its assistant message.
/// Binding is a compatibility check, not authentication. Payload must never contain credentials.
/// </summary>
public sealed record LlmProviderResponseState
{
    public int FormatVersion { get; init; } = 1;

    public required LlmTransport Transport { get; init; }

    public required string ModelId { get; init; }

    /// <summary>Opaque, non-secret account/connection scope, assigned by the adapter.</summary>
    public required string ScopeId { get; init; }

    public required string Payload { get; init; }
}

public enum LlmContextMeasurementSource
{
    Unknown,
    Estimated,
    ProviderReported,
    Mixed
}

/// <summary>Projected next input occupancy, not accumulated billing or remaining account quota.</summary>
public sealed record LlmAgentContextStatus
{
    public required string ModelId { get; init; }

    public long ContextRevision { get; init; }

    public long? MaxContextTokens { get; init; }

    /// <summary>Includes cached input, instructions, tools and attachments; excludes output reserve.</summary>
    public long? UsedContextTokens { get; init; }

    public long? ReservedOutputTokens { get; init; }

    public long? AvailableInputTokens { get; init; }

    /// <summary>Null means insufficient evidence, not false or a guarantee that input will fit.</summary>
    public bool? NeedsCompaction { get; init; }

    public bool IsEstimated { get; init; }

    public LlmContextMeasurementSource Source { get; init; }
}

public sealed record LlmAgentContextPolicy
{
    public double CompactionThresholdRatio { get; init; } = 0.85;

    public long SafetyMarginTokens { get; init; } = 4096;

    public long? ReservedOutputTokens { get; init; }

    /// <summary>Optional host cap; cannot increase a known provider limit.</summary>
    public long? MaxContextTokens { get; init; }
}

public enum LlmAgentToolExecutionMode
{
    Sequential,
    Parallel
}

/// <summary>Managed loop policy. Host tools remain responsible for their own security enforcement.</summary>
public sealed record LlmAgentExecutionOptions
{
    public int MaxModelIterations { get; init; } = 32;

    public LlmAgentToolExecutionMode ToolExecutionMode { get; init; }

    public int MaxConcurrentTools { get; init; } = 4;

    public LlmAgentContextPolicy ContextPolicy { get; init; } = new();
}

/// <summary>
/// Atomically replaces paused context and resumes the same live logical run without appending
/// another prompt. Pause IDs are single-use and do not survive session disposal/process death.
/// </summary>
public sealed record LlmAgentTurnContinuation(
    string TurnId,
    string PauseId,
    long ExpectedContextRevision,
    LlmAgentContext ReplacementContext);

public enum LlmAgentCompactionReason
{
    Threshold,
    ProviderOverflow
}

public sealed record LlmAgentContextStatusEvent(
    string ThreadId,
    string TurnId,
    LlmAgentContextStatus Status) : LlmAgentEvent(ThreadId, TurnId);

/// <summary>Ends this enumeration, not the run. All issued tools have settled before this event.</summary>
public sealed record LlmAgentCompactionRequiredEvent(
    string ThreadId,
    string TurnId,
    string PauseId,
    LlmAgentContextStatus Status,
    LlmAgentCompactionReason Reason,
    bool CanCompactHistory,
    IReadOnlyList<string> ProtectedEntryIds) : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentTurnContinuedEvent(
    string ThreadId,
    string TurnId,
    string PauseId) : LlmAgentEvent(ThreadId, TurnId);

/// <summary>Usage for one model step and this logical run, including earlier continued segments.</summary>
public sealed record LlmAgentUsageEvent(
    string ThreadId,
    string TurnId,
    string RequestId,
    LlmChatUsage? Usage,
    LlmChatUsage? RunTotals) : LlmAgentEvent(ThreadId, TurnId);

public enum LlmAgentCompletionReason
{
    Completed,
    IterationLimit,
    Refusal,
    ProviderFailure,
    Truncation,
    Interruption
}
