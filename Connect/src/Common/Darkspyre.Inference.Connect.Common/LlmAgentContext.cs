using System.Collections.ObjectModel;
using Darkspyre.Inference.Common.LLM;
using System.Diagnostics.CodeAnalysis;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>One stable record in model context, distinct from a display transcript.</summary>
/// <param name="Id">The entry's stable identity.</param>
/// <param name="TurnId">The turn the entry belongs to.</param>
/// <param name="Message">The message the entry holds.</param>
public sealed record LlmAgentContextEntry(string Id, string TurnId, LlmChatMessage Message);

/// <summary>Portable model context. An incomplete final tool group is data, not runnable input.</summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentContext
{
    /// <summary>The serialized format version of the context.</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public int FormatVersion { get; init; } = 3;

    /// <summary>The context's revision. The runtime increments it on each change to the context.</summary>
    public long Revision { get; init; }

    /// <summary>The context records in model order.</summary>
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

/// <summary>Options for capturing an agent session's model context.</summary>
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
/// <param name="Kind">The category of the omitted bytes.</param>
/// <param name="FileName">The file name of the omitted attachment.</param>
/// <param name="Index">The zero-based position of the attachment in its list.</param>
/// <param name="ToolCallId">The tool result that owns the attachment, or null for a prompt attachment.</param>
public sealed record LlmOmittedAttachment(
    LlmBinaryKind Kind,
    string FileName,
    int Index,
    string? ToolCallId = null);

/// <summary>How a context occupancy figure was measured.</summary>
public enum LlmContextMeasurementSource
{
    /// <summary>No measurement exists.</summary>
    Unknown,
    /// <summary>A local estimate alone.</summary>
    Estimated,
    /// <summary>A local estimate combined with a provider-reported count.</summary>
    Mixed
}

/// <summary>Projected next input occupancy, not accumulated billing or remaining account quota.</summary>
public sealed record LlmAgentContextStatus
{
    /// <summary>The model the projection is measured against.</summary>
    public required string ModelId { get; init; }

    /// <summary>The revision of the context the projection measures.</summary>
    public long ContextRevision { get; init; }

    /// <summary>The context window in tokens after any host cap, or null when it is unknown.</summary>
    public long? MaxContextTokens { get; init; }

    /// <summary>Includes cached input, instructions, tools and attachments; excludes output reserve.</summary>
    public long? UsedContextTokens { get; init; }

    /// <summary>The tokens held back from the window for model output, or null when it is unknown.</summary>
    public long? ReservedOutputTokens { get; init; }

    /// <summary>The input tokens left in the window after the output reserve and used context, or null when it is
    /// unknown.</summary>
    public long? AvailableInputTokens { get; init; }

    /// <summary>Null means insufficient evidence, not false or a guarantee that input will fit.</summary>
    public bool? NeedsCompaction { get; init; }

    /// <summary>How the used context tokens were measured.</summary>
    public LlmContextMeasurementSource Source { get; init; }
}

/// <summary>The context window limits and compaction behaviour the managed loop applies.</summary>
public sealed record LlmAgentContextPolicy
{
    /// <summary>The tokens held back from the context window for model output. Null, zero or less uses the model's
    /// output limit, or a default when the model has none.</summary>
    public long? ReservedOutputTokens { get; init; }

    /// <summary>Optional host cap; cannot increase a known provider limit.</summary>
    public long? MaxContextTokens { get; init; }

    /// <summary>
    /// Where compaction brings the request, as a share of the usable window (window minus output reserve).
    /// A value outside 0.05-0.80 uses the nearer bound.
    /// </summary>
    public double CompactionTargetRatio { get; init; } = 0.35;

    /// <summary>Send supplied context unchanged: report context use, never compact, return a real overflow error.</summary>
    public bool PreserveSuppliedContext { get; init; }
}

/// <summary>How the managed loop runs the tool calls of one model step.</summary>
public enum LlmAgentToolExecutionMode
{
    /// <summary>Tool calls run one at a time.</summary>
    Sequential,
    /// <summary>Tool calls run concurrently, unless a called tool requires sequential execution.</summary>
    Parallel
}

/// <summary>Managed loop policy. Host tools remain responsible for their own security enforcement.</summary>
public sealed record LlmAgentExecutionOptions
{
    /// <summary>The number of model steps that ends a turn with IterationLimit. Zero or less uses 32.</summary>
    public int MaxModelIterations { get; init; } = 32;

    /// <summary>Whether a step's tool calls run one at a time or concurrently.</summary>
    public LlmAgentToolExecutionMode ToolExecutionMode { get; init; }

    /// <summary>The most tool calls that run at once in parallel mode. Zero or less uses 4.</summary>
    public int MaxConcurrentTools { get; init; } = 4;

    /// <summary>The context limits and compaction policy for the turn.</summary>
    public LlmAgentContextPolicy ContextPolicy { get; init; } = new();

    /// <summary>Spend cap in US dollars for each turn. Null, 0 or less sets no cap.</summary>
    public decimal? MaxSpendUsd { get; init; }
}

/// <summary>
/// Atomically replaces paused context and resumes the same live logical run without appending
/// another prompt. Pause IDs are single-use and do not survive session disposal/process death.
/// </summary>
/// <param name="TurnId">The paused turn to resume.</param>
/// <param name="PauseId">The pause the continuation resolves.</param>
/// <param name="ExpectedContextRevision">The context revision the replacement was built from.</param>
/// <param name="ReplacementContext">The context that replaces the paused context.</param>
public sealed record LlmAgentTurnContinuation(
    string TurnId,
    string PauseId,
    long ExpectedContextRevision,
    LlmAgentContext ReplacementContext);

/// <summary>Why a turn paused for compaction.</summary>
public enum LlmAgentCompactionReason
{
    /// <summary>The projected context occupancy reached the compaction trigger.</summary>
    Threshold,
    /// <summary>The provider rejected the request as exceeding its context window.</summary>
    ProviderOverflow
}

/// <summary>The projected context occupancy for the next model request in a turn.</summary>
/// <param name="ThreadId">The identity of the thread.</param>
/// <param name="TurnId">The identity of the turn.</param>
/// <param name="Status">The projected occupancy.</param>
public sealed record LlmAgentContextStatusEvent(
    string ThreadId,
    string TurnId,
    LlmAgentContextStatus Status) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The identity of the turn.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>Ends this enumeration, not the run. All issued tools have settled before this event.</summary>
/// <param name="PauseId">The single-use pause a continuation must name.</param>
/// <param name="Status">The projected context occupancy that caused the pause.</param>
/// <param name="Reason">Why the turn paused.</param>
/// <param name="CanCompactHistory">True when compacting the session's history can resolve the pause.</param>
/// <param name="ProtectedEntryIds">The ids of the context entries compaction must keep unchanged.</param>
/// <param name="ThreadId">The thread the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to, or null outside a turn.</param>
public sealed record LlmAgentCompactionRequiredEvent(
    string ThreadId,
    string TurnId,
    string PauseId,
    LlmAgentContextStatus Status,
    LlmAgentCompactionReason Reason,
    bool CanCompactHistory,
    IReadOnlyList<string> ProtectedEntryIds) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>Inverse of the protected context's measured NeedsCompaction; null when unknown.</summary>
    public bool? ProtectedFits { get; init; }

    /// <summary>The identity of the paused turn.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>A paused turn resumed with replacement context.</summary>
/// <param name="ThreadId">The identity of the thread.</param>
/// <param name="TurnId">The identity of the resumed turn.</param>
/// <param name="PauseId">The pause the continuation consumed.</param>
public sealed record LlmAgentTurnContinuedEvent(
    string ThreadId,
    string TurnId,
    string PauseId) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The identity of the resumed turn.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>
/// Usage for one model step and this logical run, including earlier continued segments. Repeated observations for
/// the same request replace earlier ones. The step's completion supplies its authoritative usage.
/// </summary>
/// <param name="RequestId">The model request the usage belongs to.</param>
/// <param name="Usage">The usage of this model step, or null when not reported.</param>
/// <param name="RunTotals">The usage of the whole logical run so far, or null when not reported.</param>
/// <param name="ThreadId">The thread the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to, or null outside a turn.</param>
public sealed record LlmAgentUsageEvent(
    string ThreadId,
    string TurnId,
    string RequestId,
    LlmUsage? Usage,
    LlmUsage? RunTotals) : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The identity of the turn the usage belongs to.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    /// <summary>The model the provider reports for this step; null when it reported none.</summary>
    public string? ModelId { get; init; }
}

/// <summary>Why an agent turn ended.</summary>
public enum LlmAgentCompletionReason
{
    /// <summary>The model completed the turn naturally.</summary>
    Completed,
    /// <summary>The turn reached LlmAgentExecutionOptions.MaxModelIterations.</summary>
    IterationLimit,
    /// <summary>The model refused the request.</summary>
    Refusal,
    /// <summary>The provider failed the request.</summary>
    ProviderFailure,
    /// <summary>The model's output was cut off before it completed.</summary>
    Truncation,
    /// <summary>The turn was interrupted.</summary>
    Interruption,

    /// <summary>The model operation ended without a reported natural-completion or truncation classification.</summary>
    Unknown,

    /// <summary>The turn ended with the model's tool calls returned unrun (LlmAgentToolHandling.ReturnRequests).</summary>
    ToolRequests,

    /// <summary>The turn reached LlmAgentExecutionOptions.MaxSpendUsd.</summary>
    SpendLimit
}
