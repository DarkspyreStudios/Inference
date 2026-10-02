using System.Collections.ObjectModel;
using Darkspyre.Inference.Common.LLM;
using System.Diagnostics.CodeAnalysis;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>One stable record in model context, distinct from a display transcript.</summary>
public sealed record LlmAgentContextEntry(string Id, string TurnId, LlmChatMessage Message);

/// <summary>Settled model context. Contains no executable handlers or durable run checkpoint.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentContext
{
    [System.Text.Json.Serialization.JsonRequired]
    public int FormatVersion { get; init; } = 3;

    public long Revision { get; init; }

    public IReadOnlyList<LlmAgentContextEntry> Entries { get; init; } = [];

    private IReadOnlyList<InferenceResourceReference> _selectedResources = [];

    /// <summary>Copied caller-owned membership, not an independently mutable selection catalog.</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public IReadOnlyList<InferenceResourceReference> SelectedResources
    {
        get => _selectedResources;
        init => _selectedResources = CopySelection(value);
    }

    private InferenceResourceSelectionVersion? _resourceSelectionVersion;

    /// <summary>Null means no observed caller version, not revision zero or replacement authority.</summary>
    public InferenceResourceSelectionVersion? ResourceSelectionVersion
    {
        get => _resourceSelectionVersion;
        init
        {
            if (value is null) { _resourceSelectionVersion = null; return; }
            ArgumentException.ThrowIfNullOrEmpty(value.WorkspaceId);
            ArgumentException.ThrowIfNullOrEmpty(value.SessionId);
            ArgumentOutOfRangeException.ThrowIfNegative(value.Revision);
            _resourceSelectionVersion = new(value.WorkspaceId, value.SessionId, value.Revision);
        }
    }

    internal void ValidateResourceSelection()
    {
        if (ResourceSelectionVersion is { } version
            && SelectedResources.Any(reference => !string.Equals(reference.WorkspaceId, version.WorkspaceId, StringComparison.Ordinal)))
            throw new ArgumentException("Context resource selection belongs to a different workspace.");
    }

    private static ReadOnlyCollection<InferenceResourceReference> CopySelection(IReadOnlyList<InferenceResourceReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        var seen = new HashSet<InferenceResourceReference>();
        var snapshot = new InferenceResourceReference[references.Count];
        string? workspaceId = null;
        for (int index = 0; index < references.Count; index++)
        {
            InferenceResourceReference reference = references[index];
            ArgumentNullException.ThrowIfNull(reference);
            ArgumentException.ThrowIfNullOrEmpty(reference.WorkspaceId);
            ArgumentException.ThrowIfNullOrEmpty(reference.Id);
            workspaceId ??= reference.WorkspaceId;
            if (!string.Equals(workspaceId, reference.WorkspaceId, StringComparison.Ordinal) || !seen.Add(reference))
                throw new ArgumentException("Context selection must contain unique references from one workspace.", nameof(references));
            snapshot[index] = new(reference.WorkspaceId, reference.Id);
        }
        return Array.AsReadOnly(snapshot);
    }
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

public enum LlmContextMeasurementSource
{
    Unknown,
    Estimated,
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
    LlmAgentContextStatus Status) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>Ends this enumeration, not the run. All issued tools have settled before this event.</summary>
public sealed record LlmAgentCompactionRequiredEvent(
    string ThreadId,
    string TurnId,
    string PauseId,
    LlmAgentContextStatus Status,
    LlmAgentCompactionReason Reason,
    bool CanCompactHistory,
    IReadOnlyList<string> ProtectedEntryIds) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

public sealed record LlmAgentTurnContinuedEvent(
    string ThreadId,
    string TurnId,
    string PauseId) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>
/// Usage for one model step and this logical run, including earlier continued segments. Repeated observations for
/// the same request replace earlier ones. The step's completion supplies its authoritative usage.
/// </summary>
public sealed record LlmAgentUsageEvent(
    string ThreadId,
    string TurnId,
    string RequestId,
    LlmUsage? Usage,
    LlmUsage? RunTotals) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

public enum LlmAgentCompletionReason
{
    Completed,
    IterationLimit,
    Refusal,
    ProviderFailure,
    Truncation,
    Interruption,

    /// <summary>The model operation ended without a reported natural-completion or truncation classification.</summary>
    Unknown
}
