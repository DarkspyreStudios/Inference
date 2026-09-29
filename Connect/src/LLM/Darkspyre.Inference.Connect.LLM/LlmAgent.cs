using System.Runtime.CompilerServices;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

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

/// <summary>A host-implemented dynamic tool exposed to an agent session.</summary>
public sealed record LlmAgentToolDefinition
{
    public required string Name { get; init; }

    public string? Description { get; init; }

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

/// <summary>A dynamic tool invocation sent from the agent to the host.</summary>
public sealed record LlmAgentToolCall(
    string ThreadId,
    string TurnId,
    string CallId,
    string? Namespace,
    string Name,
    JsonElement Arguments);

public delegate Task<LlmToolOutput> LlmAgentToolHandler(
    LlmAgentToolCall call,
    CancellationToken cancellationToken);

/// <summary>Who decides what a provider-owned runtime may do while it works.</summary>
public enum LlmAgentRuntimePolicyKind
{
    /// <summary>The host selects the filesystem, network, approval and background-work limits.</summary>
    HostControlled,

    /// <summary>The runtime applies the policy configured in the provider's own settings.</summary>
    ProviderConfigured
}

/// <summary>
/// The execution policy a provider-owned runtime runs under. Exactly two variants exist:
/// <see cref="LlmAgentHostRuntimePolicy"/> and <see cref="LlmAgentProviderRuntimePolicy"/>.
/// <see cref="LlmAgentCapabilities.SupportedRuntimePolicies"/> lists the variants a runtime accepts.
/// </summary>
public abstract record LlmAgentRuntimePolicy
{
    private protected LlmAgentRuntimePolicy()
    {
    }

    public abstract LlmAgentRuntimePolicyKind Kind { get; }
}

/// <summary>Limits the host selects and the runtime enforces.</summary>
public sealed record LlmAgentHostRuntimePolicy : LlmAgentRuntimePolicy
{
    public override LlmAgentRuntimePolicyKind Kind => LlmAgentRuntimePolicyKind.HostControlled;

    /// <summary>Every filesystem root the provider-owned runtime may write beneath.</summary>
    public IReadOnlyList<string> WritableRoots { get; init; } = [];

    /// <summary>Whether provider-owned commands may use the network.</summary>
    public bool NetworkAllowed { get; init; } = true;

    public LlmAgentSandboxMode Sandbox { get; init; } = LlmAgentSandboxMode.WorkspaceWrite;

    public LlmAgentApprovalPolicy ApprovalPolicy { get; init; } = LlmAgentApprovalPolicy.OnRequest;

    /// <summary>
    /// Lets the runtime start subagents and output monitors of its own. Off by default. Only a runtime whose
    /// <see cref="LlmAgentCapabilities.ResourceObservation"/> is Supported accepts it.
    /// </summary>
    public bool AllowRuntimeBackgroundWork { get; init; }
}

/// <summary>
/// Explicit adoption of the policy configured in the provider's own settings, including its background
/// behavior. Connect does not read, edit or strengthen that configuration, and makes no promise about the
/// filesystem, network or background limits it applies.
/// </summary>
public sealed record LlmAgentProviderRuntimePolicy : LlmAgentRuntimePolicy
{
    public override LlmAgentRuntimePolicyKind Kind => LlmAgentRuntimePolicyKind.ProviderConfigured;
}

/// <summary>
/// Settings only a provider-owned runtime reads: where it works, whether its conversation persists, and
/// the policy it runs under. Native runtimes require them; managed transports reject them.
/// </summary>
public sealed record LlmAgentNativeRuntimeOptions
{
    public required string WorkingDirectory { get; init; }

    public bool Ephemeral { get; init; }

    /// <summary>
    /// The execution policy. Defaults to host-controlled limits. A runtime rejects a variant missing from
    /// its <see cref="LlmAgentCapabilities.SupportedRuntimePolicies"/> before any effect.
    /// </summary>
    public LlmAgentRuntimePolicy Policy { get; init; } = new LlmAgentHostRuntimePolicy();
}

public sealed record LlmAgentSessionOptions
{
    /// <summary>
    /// Settled context to start from. <see cref="LlmAgentCapabilities.ContextImport"/> reports how the runtime
    /// takes it.
    /// </summary>
    public LlmAgentContext? Context { get; init; }

    /// <summary>
    /// Set to <see cref="LlmUnboundContinuationState.Omit"/> when <see cref="Context"/> carries retained history
    /// from another model or provider. An opening-context start carries no state, so it requires Omit.
    /// </summary>
    public LlmUnboundContinuationState UnboundContinuationState { get; init; } = LlmUnboundContinuationState.Reject;

    public string? ThreadId { get; init; }

    public string? SessionId { get; init; }

    /// <summary>Null selects managed defaults. Native adapters reject supplied managed options.</summary>
    public LlmAgentExecutionOptions? ExecutionOptions { get; init; }

    /// <summary>Required by provider-owned runtimes; managed transports reject it.</summary>
    public LlmAgentNativeRuntimeOptions? Runtime { get; init; }

    public required string ModelId { get; init; }

    public string? Instructions { get; init; }

    public IReadOnlyList<LlmAgentToolDefinition> Tools { get; init; } = [];

    public LlmAgentToolHandler? ToolHandler { get; init; }

    /// <summary>Output Connect retains per resource when the runtime keeps none; the earliest cursor advances past it.</summary>
    public long ResourceOutputRetentionBytes { get; init; } = 1_048_576;
}

public sealed record LlmAgentResumeOptions
{
    public string? ModelId { get; init; }

    public string? Instructions { get; init; }

    /// <summary>
    /// Runtime settings used whole, or null for the resume defaults: the current directory as working
    /// directory, and a host-controlled policy with that directory as its only writable root,
    /// <see cref="LlmAgentSandboxMode.WorkspaceWrite"/>, network allowed, <see cref="LlmAgentApprovalPolicy.OnRequest"/>
    /// and no runtime background work. A runtime that accepts only a provider-configured policy rejects null.
    /// </summary>
    public LlmAgentNativeRuntimeOptions? Runtime { get; init; }

    /// <summary>
    /// Host tools to expose after resuming. Providers that persist tool definitions may reuse the
    /// stored definitions when this collection is empty.
    /// </summary>
    public IReadOnlyList<LlmAgentToolDefinition> Tools { get; init; } = [];

    public LlmAgentToolHandler? ToolHandler { get; init; }

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
    Failed,
    /// <summary>Terminal output is available but its natural completion or truncation is unknown.</summary>
    Unknown
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

/// <summary>Replaces the previous partial structured object for this item.</summary>
public sealed record LlmAgentStructuredOutputSnapshotEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    JsonElement Content)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentReasoningDeltaEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Delta,
    bool IsSummary)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentMessageCompletedEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Text)
    : LlmAgentEvent(ThreadId, TurnId);

/// <summary>
/// A tool call observed during a turn. <see cref="Output"/> is the result the runtime reported for it, or
/// null when none was reported. It is an observation only: a tool the runtime owns never reaches a host
/// handler through this event.
/// </summary>
public sealed record LlmAgentToolEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string? Namespace,
    string Tool,
    JsonElement Arguments,
    bool? Success,
    string Status,
    LlmToolOutput? Output = null)
    : LlmAgentEvent(ThreadId, TurnId);

/// <summary>Adapter-owned progress text. It is not assistant transcript content.</summary>
public sealed record LlmAgentActivityEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Activity)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentWarningEvent(
    string ThreadId,
    string TurnId,
    string Message)
    : LlmAgentEvent(ThreadId, TurnId);

public sealed record LlmAgentTurnCompletedEvent(
    string ThreadId,
    string TurnId,
    LlmAgentCompletionReason Reason,
    string? Error)
    : LlmAgentEvent(ThreadId, TurnId)
{
    /// <summary>
    /// Derived from <see cref="Reason"/>: <c>Completed</c> for a completed turn, <c>Interrupted</c> for an
    /// interruption, <c>Unknown</c> for unreported termination, and <c>Failed</c> for every other reason.
    /// </summary>
    public LlmAgentTurnStatus Status => Reason switch
    {
        LlmAgentCompletionReason.Completed => LlmAgentTurnStatus.Completed,
        LlmAgentCompletionReason.Interruption => LlmAgentTurnStatus.Interrupted,
        LlmAgentCompletionReason.Unknown => LlmAgentTurnStatus.Unknown,
        _ => LlmAgentTurnStatus.Failed
    };
}

/// <summary>
/// A transport-specific agent client exposed through provider-neutral session semantics. Hosts open
/// sessions through <see cref="LlmAgentProviderService"/>, which resolves the connection by its ID.
/// </summary>
public interface ILlmAgentClient
{
    LlmTransport Transport { get; }

    Task<ILlmAgentSession> StartSessionAsync(
        string connectionId,
        LlmConnection connection,
        LlmAgentSessionOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>An agent client whose runtime can reopen a persisted provider thread by its ID.</summary>
public interface ILlmAgentResumeClient : ILlmAgentClient
{
    Task<ILlmAgentSession> ResumeSessionAsync(
        string connectionId,
        LlmConnection connection,
        string threadId,
        LlmAgentResumeOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>An agent client whose runtime can fork a persisted provider thread.</summary>
public interface ILlmAgentForkClient : ILlmAgentClient
{
    Task<ILlmAgentSession> ForkSessionAsync(
        string connectionId,
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
