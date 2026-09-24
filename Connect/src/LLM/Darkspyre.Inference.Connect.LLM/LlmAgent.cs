using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Filesystem isolation requested for a provider-owned agent runtime.</summary>
public enum LlmAgentSandboxMode
{
    ReadOnly,
    WorkspaceWrite,
    DangerFullAccess
}

/// <summary>When an agent must ask the host before performing privileged work.</summary>
public enum LlmAgentApprovalPolicy
{
    Untrusted,
    OnRequest,
    Never
}

public enum LlmAgentApprovalKind
{
    CommandExecution,
    FileChange
}

public enum LlmAgentApprovalDecision
{
    Accept,
    AcceptForSession,
    Decline,
    Cancel
}

/// <summary>A host-implemented dynamic tool exposed to an agent session.</summary>
public sealed record LlmAgentToolDefinition
{
    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public required JsonElement InputSchema { get; init; }

    /// <summary>
    /// Requests provider-enforced schema adherence. The schema must then meet the provider's strict
    /// rules; for OpenAI that means every object closes <c>additionalProperties</c> and lists every
    /// property as required. A host schema describes what the tool accepts, so the default is false.
    /// </summary>
    public bool Strict { get; init; }

    public bool DeferLoading { get; init; }

    /// <summary>Forces an entire managed tool batch to execute sequentially.</summary>
    public bool RequiresSequentialExecution { get; init; }
}

public abstract record LlmAgentContent;

public sealed record LlmAgentTextContent(string Text) : LlmAgentContent;

public sealed record LlmAgentImageContent(string ImageUrl) : LlmAgentContent;

public sealed record LlmAgentAudioContent(string AudioUrl) : LlmAgentContent;

/// <summary>A dynamic tool invocation sent from the agent to the host.</summary>
public sealed record LlmAgentToolCall(
    string ThreadId,
    string TurnId,
    string CallId,
    string? Namespace,
    string Name,
    JsonElement Arguments);


public sealed record LlmAgentApprovalRequest
{
    public required LlmAgentApprovalKind Kind { get; init; }

    public required string ThreadId { get; init; }

    public required string TurnId { get; init; }

    public required string ItemId { get; init; }

    /// <summary>Provider subtype such as command, network, or stdin when supplied.</summary>
    public string? ProviderKind { get; init; }

    public string? Reason { get; init; }

    public string? Command { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? GrantRoot { get; init; }

    public IReadOnlyList<LlmAgentApprovalDecision> AvailableDecisions { get; init; } = [];
}

public delegate ValueTask<LlmToolOutput> LlmAgentToolHandler(
    LlmAgentToolCall call,
    CancellationToken cancellationToken);

public delegate ValueTask<LlmAgentApprovalDecision> LlmAgentApprovalHandler(
    LlmAgentApprovalRequest request,
    CancellationToken cancellationToken);

public sealed record LlmAgentSessionHandlers
{
    public LlmAgentToolHandler? ToolHandler { get; init; }

    public LlmAgentApprovalHandler? ApprovalHandler { get; init; }
}

public sealed record LlmAgentSessionOptions
{
    /// <summary>
    /// Settled context to start from. <see cref="LlmAgentCapabilities.ContextImport"/> reports how the runtime
    /// takes it.
    /// </summary>
    public LlmAgentContext? Context { get; init; }

    /// <summary>
    /// Set to <see cref="LlmUnboundResponseState.Omit"/> when <see cref="Context"/> carries retained history
    /// from another model or provider. An opening-context start carries no state, so it requires Omit.
    /// </summary>
    public LlmUnboundResponseState UnboundResponseState { get; init; } = LlmUnboundResponseState.Reject;

    public string? ThreadId { get; init; }

    public string? SessionId { get; init; }

    /// <summary>Null selects managed defaults. Native adapters reject supplied managed options.</summary>
    public LlmAgentExecutionOptions? ExecutionOptions { get; init; }

    public required string ModelId { get; init; }

    public required string WorkingDirectory { get; init; }

    public string? Instructions { get; init; }

    /// <summary>Every filesystem root the provider-owned runtime may write beneath.</summary>
    public IReadOnlyList<string> WritableRoots { get; init; } = [];

    /// <summary>Whether provider-owned commands may use the network.</summary>
    public bool NetworkAllowed { get; init; } = true;

    public IReadOnlyList<LlmAgentToolDefinition> Tools { get; init; } = [];

    public LlmAgentSandboxMode Sandbox { get; init; } = LlmAgentSandboxMode.WorkspaceWrite;

    public LlmAgentApprovalPolicy ApprovalPolicy { get; init; } = LlmAgentApprovalPolicy.OnRequest;

    public bool Ephemeral { get; init; }

    public LlmAgentSessionHandlers Handlers { get; init; } = new();

    /// <summary>
    /// Lets the runtime start subagents and output monitors of its own. Off by default. Only a runtime whose
    /// <see cref="LlmAgentCapabilities.ResourceObservation"/> is Supported accepts it.
    /// </summary>
    public bool AllowRuntimeBackgroundWork { get; init; }

    /// <summary>Output Connect retains per resource when the runtime keeps none; the earliest cursor advances past it.</summary>
    public long ResourceOutputRetentionBytes { get; init; } = 1_048_576;
}

public sealed record LlmAgentResumeOptions
{
    public string? ModelId { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? Instructions { get; init; }

    /// <summary>
    /// Replacement filesystem roots for the resumed runtime, or null to use the working directory.
    /// </summary>
    public IReadOnlyList<string>? WritableRoots { get; init; }

    /// <summary>Replacement network choice, or null to use the provider's resume behavior.</summary>
    public bool? NetworkAllowed { get; init; }

    /// <summary>
    /// Host tools to expose after resuming. Providers that persist tool definitions may reuse the
    /// stored definitions when this collection is empty.
    /// </summary>
    public IReadOnlyList<LlmAgentToolDefinition> Tools { get; init; } = [];

    public LlmAgentSandboxMode? Sandbox { get; init; }

    public LlmAgentApprovalPolicy? ApprovalPolicy { get; init; }

    public LlmAgentSessionHandlers Handlers { get; init; } = new();

    /// <summary>
    /// Lets the runtime start subagents and output monitors of its own. Off by default. Only a runtime whose
    /// <see cref="LlmAgentCapabilities.ResourceObservation"/> is Supported accepts it.
    /// </summary>
    public bool AllowRuntimeBackgroundWork { get; init; }

    /// <summary>Output Connect retains per resource when the runtime keeps none; the earliest cursor advances past it.</summary>
    public long ResourceOutputRetentionBytes { get; init; } = 1_048_576;
}

public sealed record LlmAgentTurnRequest
{
    /// <summary>
    /// Null uses session base instructions; empty clears them for this run; nonempty replaces them.
    /// Applies through tool iterations and compaction continuations, never to subsequent runs.
    /// Requires SupportsAgentTurnInstructions; unsupported adapters reject even an empty override.
    /// </summary>
    public string? Instructions { get; init; }

    public required IReadOnlyList<LlmAgentContent> Input { get; init; }

    /// <summary>Provider-neutral binary content supplied with this turn.</summary>
    public IReadOnlyList<LlmAttachment> Attachments { get; init; } = [];

    public string? ModelId { get; init; }

    /// <summary>The requested reasoning level; null leaves the provider's default in place.</summary>
    public Darkspyre.Inference.Common.LLM.LlmReasoningLevel? ReasoningLevel { get; init; }

    public JsonElement? OutputSchema { get; init; }
}

public enum LlmAgentTurnStatus
{
    Completed,
    Interrupted,
    Failed
}

public abstract record LlmAgentEvent(string ThreadId, string TurnId);

public sealed record LlmAgentTurnStartedEvent(string ThreadId, string TurnId)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentTextDeltaEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Delta)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentReasoningDeltaEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Delta,
    bool IsSummary)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentPlanDeltaEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Delta)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentMessageCompletedEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Text)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentCommandEvent
    : LlmAgentEvent
{
    public LlmAgentCommandEvent(string threadId, string turnId)
        : base(threadId, turnId)
    {
    }

    public required string ItemId { get; init; }

    public required string Command { get; init; }

    public required string WorkingDirectory { get; init; }

    public required string Status { get; init; }

    public string? Output { get; init; }

    public int? ExitCode { get; init; }
}

public sealed record LlmAgentFileChange(string Path, string Kind, string Diff);

public sealed record LlmAgentFileChangeEvent
    : LlmAgentEvent
{
    public LlmAgentFileChangeEvent(string threadId, string turnId)
        : base(threadId, turnId)
    {
    }

    public required string ItemId { get; init; }

    public required string Status { get; init; }

    public IReadOnlyList<LlmAgentFileChange> Changes { get; init; } = [];
}

public sealed record LlmAgentMcpToolEvent
    : LlmAgentEvent
{
    public LlmAgentMcpToolEvent(string threadId, string turnId)
        : base(threadId, turnId)
    {
    }

    public required string ItemId { get; init; }

    public required string Server { get; init; }

    public required string Tool { get; init; }

    public required string Status { get; init; }

    public required JsonElement Arguments { get; init; }

    public JsonElement? Result { get; init; }

    public string? Error { get; init; }
}

public sealed record LlmAgentWebSearchEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Query,
    IReadOnlyList<JsonElement> Results)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentImageViewEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Path)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentCollaborationEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Tool,
    string Status,
    string SenderThreadId,
    IReadOnlyList<string> ReceiverThreadIds,
    string? Prompt)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentSubAgentActivityEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Kind,
    string AgentThreadId,
    string AgentPath)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentToolEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string? Namespace,
    string Tool,
    JsonElement Arguments,
    bool? Success,
    string Status)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentCompactedEvent(string ThreadId, string TurnId)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentWarningEvent(
    string ThreadId,
    string TurnId,
    string Message)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentTurnCompletedEvent(
    string ThreadId,
    string TurnId,
    LlmAgentTurnStatus Status,
    string? Error)
    : LlmAgentEvent(ThreadId, TurnId)
{
    public LlmAgentCompletionReason Reason { get; init; } = Status switch
    {
        LlmAgentTurnStatus.Completed => LlmAgentCompletionReason.Completed,
        LlmAgentTurnStatus.Interrupted => LlmAgentCompletionReason.Interruption,
        _ => LlmAgentCompletionReason.ProviderFailure
    };
}

/// <summary>A transport-specific agent client exposed through provider-neutral session semantics.</summary>
public interface ILlmAgentClient
{
    LlmTransport Transport { get; }

    Task<ILlmAgentSession> StartSessionAsync(
        LlmConnection connection,
        LlmAgentSessionOptions options,
        CancellationToken cancellationToken = default);

    Task<ILlmAgentSession> ResumeSessionAsync(
        LlmConnection connection,
        string threadId,
        LlmAgentResumeOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<ILlmAgentSession> ForkSessionAsync(
        LlmConnection connection,
        string threadId,
        string? lastTurnId = null,
        LlmAgentResumeOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface ILlmAgentSession : IAsyncDisposable
{
    /// <summary>
    /// Measures an arbitrary replacement without installing it. Uses base configuration when idle,
    /// frozen run configuration (and queued steering) when paused; rejects active execution.
    /// Native-owned context returns unsupported. Acceptance remains a separate revision-checked operation.
    /// </summary>
    Task<LlmRequestAccounting> MeasureContextAsync(
        LlmAgentContext proposedContext,
        CancellationToken cancellationToken = default);

    string ThreadId { get; }

    /// <summary>Root session identifier shared by a thread and its forks.</summary>
    string SessionId { get; }

    Task<LlmAgentCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>Prospective requests are idle-only and do not mutate context or generate output.</summary>
    Task<LlmAgentContextStatus> GetContextStatusAsync(
        LlmAgentTurnRequest? prospectiveTurn = null,
        CancellationToken cancellationToken = default);

    /// <summary>Capture while idle, paused, or after cancellation has settled.</summary>
    Task<LlmAgentContext> CaptureContextAsync(
        LlmAgentContextCaptureOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Idle-only compare-and-swap replacement. The engine assigns the next revision.</summary>
    Task ReplaceContextAsync(
        LlmAgentContext context,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(
        LlmAgentTurnContinuation continuation,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(
        LlmAgentTurnRequest request,
        CancellationToken cancellationToken = default);

    Task SteerAsync(
        string turnId,
        IReadOnlyList<LlmAgentContent> input,
        CancellationToken cancellationToken = default);

    Task InterruptAsync(string turnId, CancellationToken cancellationToken = default);

    Task CompactAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Session-lifetime events outside host turns: resource changes, output hints and provider-started turns.
    /// One reader at a time; the sequence ends when the session is disposed.
    /// </summary>
    IAsyncEnumerable<LlmAgentEvent> ObserveAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>Nondestructive read. A null cursor reads from the earliest retained output.</summary>
    Task<LlmAgentResourceOutput> ReadResourceOutputAsync(
        string resourceId,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId, CancellationToken cancellationToken = default);
}
