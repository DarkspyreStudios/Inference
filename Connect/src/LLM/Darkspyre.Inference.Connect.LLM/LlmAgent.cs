using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
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
public sealed record LlmAgentToolDefinition : LlmAgentClientToolDefinition
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required JsonElement InputSchema { get; init; }

    public bool DeferLoading { get; init; }

    /// <summary>Forces an entire managed tool batch to execute sequentially.</summary>
    public bool RequiresSequentialExecution { get; init; }

    /// <summary>Compaction keeps this tool's calls and results in the current turn whole.</summary>
    public bool ProtectedResults { get; init; }
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
/// the policy it runs under. A runtime that does not read them accepts and ignores them.
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
    /// <remarks>
    /// Continuation state the selected model, provider and account cannot replay is left out and counted, so a
    /// history from any provider or connection continues here.
    /// </remarks>
    public LlmAgentContext? Context { get; init; }

    public string? ThreadId { get; init; }

    public string? SessionId { get; init; }

    /// <summary>Null selects the defaults. Each adapter reads the members it uses.</summary>
    public LlmAgentExecutionOptions? ExecutionOptions { get; init; }

    /// <summary>Read by provider-owned runtimes; a managed runtime accepts and ignores it.</summary>
    public LlmAgentNativeRuntimeOptions? Runtime { get; init; }

    public required string ModelId { get; init; }

    public string? Instructions { get; init; }

    public IReadOnlyList<LlmAgentClientToolDefinition> Tools { get; init; } = [];

    /// <summary>Borrowed runtime services. The session releases references after admitted work drains.</summary>
    [JsonIgnore]
    public LlmAgentCallerServices? CallerServices { get; init; }

    public IReadOnlyList<LlmHostedTool> HostedTools { get; init; } = [];

    public int? MaxHostedTurns { get; init; }

    public LlmAgentToolHandler? ToolHandler { get; init; }

    /// <summary>Execute runs the handler; ReturnRequests ends the turn with the calls unrun.</summary>
    public LlmAgentToolHandling ToolHandling { get; init; } = LlmAgentToolHandling.Execute;

    /// <summary>Folders that each hold a SKILL.md. The model can load any of them with the load_skill tool.</summary>
    public IReadOnlyList<string> SkillFolders { get; init; } = [];

    /// <summary>Models tried in order when the session model is overloaded or unavailable for a turn.</summary>
    public IReadOnlyList<string> FallbackModelIds { get; init; } = [];

    /// <summary>Output Connect retains per resource when the runtime keeps none; the earliest cursor advances past it.</summary>
    public long ResourceOutputRetentionBytes { get; init; } = 1_048_576;
}

/// <summary>What a conversation does with the tool calls the model makes.</summary>
public enum LlmAgentToolHandling
{
    /// <summary>Inference runs the supplied handler and continues the turn.</summary>
    Execute,

    /// <summary>The turn ends with the model's tool calls returned unrun, with their validation results.</summary>
    ReturnRequests
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
    public IReadOnlyList<LlmAgentClientToolDefinition> Tools { get; init; } = [];

    /// <summary>Borrowed runtime services. The session releases references after admitted work drains.</summary>
    [JsonIgnore]
    public LlmAgentCallerServices? CallerServices { get; init; }

    public IReadOnlyList<LlmHostedTool> HostedTools { get; init; } = [];

    public int? MaxHostedTurns { get; init; }

    public LlmAgentToolHandler? ToolHandler { get; init; }

    /// <summary>Output Connect retains per resource when the runtime keeps none; the earliest cursor advances past it.</summary>
    public long ResourceOutputRetentionBytes { get; init; } = 1_048_576;
}

public sealed record LlmAgentTurnRequest
{
    /// <summary>
    /// Null uses session instructions; empty clears them for this turn; nonempty replaces them.
    /// Applies through the turn's tool iterations, never to later turns.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>May be empty: the turn then sends the context as it is.</summary>
    public IReadOnlyList<LlmAgentContent> Input { get; init; } = [];

    /// <summary>Provider-neutral binary content supplied with this turn.</summary>
    public IReadOnlyList<LlmMessageAttachment> Attachments { get; init; } = [];

    public string? ModelId { get; init; }

    /// <summary>The requested reasoning level; null leaves the provider's default in place.</summary>
    public Darkspyre.Inference.Common.LLM.LlmReasoningLevel? ReasoningLevel { get; init; }

    public JsonElement? OutputSchema { get; init; }

    /// <summary>Sampling temperature. A value the selected model cannot use is not sent.</summary>
    public double? Temperature { get; init; }

    /// <summary>Output token cap for each model step. A value the selected route cannot enforce is not sent.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Tool choice for the turn's first model step; later steps use Auto. Not sent without tools.</summary>
    public LlmToolChoice? ToolChoice { get; init; }
}

public enum LlmAgentTurnStatus
{
    Completed,
    Interrupted,
    Failed,
    /// <summary>Terminal output is available but its natural completion or truncation is unknown.</summary>
    Unknown
}

public abstract record LlmAgentEvent(string ThreadId, string? TurnId)
{
    public virtual string ThreadId { get; init; } = ThreadId;
    public virtual string? TurnId { get; init; } = TurnId;
}

public sealed record LlmAgentTurnStartedEvent(string ThreadId, string TurnId)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

public sealed record LlmAgentTextDeltaEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Delta)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>Replaces the previous partial structured object for this item.</summary>
public sealed record LlmAgentStructuredOutputSnapshotEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    JsonElement Content)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

public sealed record LlmAgentReasoningDeltaEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Delta,
    bool IsSummary)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

public sealed record LlmAgentMessageCompletedEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Text)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

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
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>An attempt failed. Unfinished output of the failed attempt is discarded; NextAttempt, when set, starts after Delay.</summary>
/// <param name="ThreadId">The owning conversation.</param>
/// <param name="TurnId">The active turn.</param>
/// <param name="ItemId">The failed model attempt.</param>
/// <param name="NextAttempt">The next attempt, or null when no automatic replacement follows.</param>
/// <gotcha>Completed messages and application-tool results remain intact. This event is not transcript content.</gotcha>
public sealed record LlmAgentResponseRecoveryEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    int? NextAttempt)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The normalized identity of the active turn.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    /// <summary>The wait before NextAttempt starts.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>The failure that caused the retry, with credentials removed.</summary>
    public string? Error { get; init; }
}

/// <summary>Adapter-owned progress text. It is not assistant transcript content.</summary>
public sealed record LlmAgentActivityEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Activity)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

public sealed record LlmAgentWarningEvent(
    string ThreadId,
    string TurnId,
    string Message)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

public sealed record LlmAgentTurnCompletedEvent(
    string ThreadId,
    string TurnId,
    LlmAgentCompletionReason Reason,
    string? Error)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }

    /// <summary>
    /// Whether the provider refused the turn because its input exceeded the model's context window. The provider's
    /// conversation then holds input it cannot read again; a host can start a new conversation from its own history.
    /// </summary>
    public bool IsContextOverflow { get; init; }

    /// <summary>The final answer's check against the turn's OutputSchema; null when the turn requested none.</summary>
    public LlmStructuredOutputValidation? StructuredOutput { get; init; }

    /// <summary>
    /// Derived from <see cref="Reason"/>: <c>Completed</c> for a completed turn or returned tool requests,
    /// <c>Interrupted</c> for an interruption, <c>Unknown</c> for unreported termination, and <c>Failed</c> for
    /// every other reason.
    /// </summary>
    public LlmAgentTurnStatus Status => Reason switch
    {
        LlmAgentCompletionReason.Completed or LlmAgentCompletionReason.ToolRequests => LlmAgentTurnStatus.Completed,
        LlmAgentCompletionReason.Interruption => LlmAgentTurnStatus.Interrupted,
        LlmAgentCompletionReason.Unknown => LlmAgentTurnStatus.Unknown,
        _ => LlmAgentTurnStatus.Failed
    };
}

/// <summary>
/// A transport-specific agent client exposed through provider-neutral session semantics. Hosts open
/// sessions through <see cref="LlmAgentProviderService"/>, which resolves the connection by its ID.
/// </summary>
internal interface ILlmAgentClient
{
    LlmTransport Transport { get; }

    Task<ILlmAgentSession> StartSessionAsync(
        string connectionId,
        LlmConnection connection,
        LlmAgentSessionOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Who compacts a conversation when its context fills.</summary>
public enum LlmCompactionResponsibility
{
    /// <summary>Compaction happens inside Inference or the provider; the Host never supplies a replacement.</summary>
    Provider,

    /// <summary>The conversation pauses with LlmAgentCompactionRequiredEvent; the Host supplies the replacement.</summary>
    Host
}

/// <summary>
/// One caller-disposable agent session. Sessions opened through <see cref="LlmAgentProviderService"/>
/// also retire with that facade. Direct agent-client callers own their sessions.
/// </summary>
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

    /// <summary>Who compacts this conversation when its context fills. The Host reads this directly.</summary>
    LlmCompactionResponsibility CompactionResponsibility { get; }

    /// <summary>
    /// Compacts the conversation now with the compactor a send uses, whatever the measure says.
    /// Returns the compaction's event, or null when there is nothing to compact.
    /// </summary>
    /// <gotcha>Refused while a turn runs (InvalidOperationException) and where SupportsCompactNow is Unsupported
    /// (NotSupportedException).</gotcha>
    Task<LlmAgentCompactedEvent?> CompactNowAsync(CancellationToken cancellationToken = default);

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

    /// <summary>Creates an idle or paused replacement proposal without installing it.</summary>
    Task<LlmAgentContext> CompactWithProviderAsync(
        LlmProviderCompactionRequest request,
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
