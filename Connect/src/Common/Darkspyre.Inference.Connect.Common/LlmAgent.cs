using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Filesystem isolation requested for a provider-owned agent runtime.</summary>
public enum LlmAgentSandboxMode
{
    /// <summary>The runtime may read files but not write them.</summary>
    ReadOnly,
    /// <summary>The runtime may write beneath its writable roots.</summary>
    WorkspaceWrite,
    /// <summary>The runtime has unrestricted filesystem access.</summary>
    DangerFullAccess
}

/// <summary>When an agent must ask the host before performing privileged work.</summary>
public enum LlmAgentApprovalPolicy
{
    /// <summary>The agent asks before running any command not known to be safe.</summary>
    Untrusted,
    /// <summary>The agent decides when to ask.</summary>
    OnRequest,
    /// <summary>The agent never asks for approval.</summary>
    Never
}

/// <summary>A host-implemented dynamic tool exposed to an agent session.</summary>
public sealed record LlmAgentToolDefinition : LlmAgentClientToolDefinition
{
    /// <summary>The name the model calls the tool by.</summary>
    public required string Name { get; init; }

    /// <summary>Text that tells the model what the tool does.</summary>
    public string? Description { get; init; }

    /// <summary>JSON Schema for the tool's arguments.</summary>
    public required JsonElement InputSchema { get; init; }

    /// <summary>Requests deferred loading, so the provider loads the definition only when the model needs it. A provider without deferred loading rejects the tool.</summary>
    public bool DeferLoading { get; init; }

    /// <summary>Forces an entire managed tool batch to execute sequentially.</summary>
    public bool RequiresSequentialExecution { get; init; }

    /// <summary>Compaction keeps this tool's calls and results in the current turn whole.</summary>
    public bool ProtectedResults { get; init; }
}

/// <summary>One piece of user input sent to an agent turn.</summary>
public abstract record LlmAgentContent;

/// <summary>Text input for an agent turn.</summary>
/// <param name="Text">The input text.</param>
public sealed record LlmAgentTextContent(string Text) : LlmAgentContent;

/// <summary>Image input for an agent turn, given by URL.</summary>
/// <param name="ImageUrl">The image location.</param>
public sealed record LlmAgentImageContent(string ImageUrl) : LlmAgentContent;

/// <summary>A dynamic tool invocation sent from the agent to the host.</summary>
/// <param name="ThreadId">The conversation the call belongs to.</param>
/// <param name="TurnId">The turn the call belongs to.</param>
/// <param name="CallId">The identity of the call.</param>
/// <param name="Namespace">The namespace of the called tool, or null when it has none.</param>
/// <param name="Name">The name of the called tool.</param>
/// <param name="Arguments">The call arguments.</param>
public sealed record LlmAgentToolCall(
    string ThreadId,
    string TurnId,
    string CallId,
    string? Namespace,
    string Name,
    JsonElement Arguments);

/// <summary>Runs a host tool call made by the agent and returns its output.</summary>
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

    /// <summary>The policy variant this record carries.</summary>
    public abstract LlmAgentRuntimePolicyKind Kind { get; }
}

/// <summary>Limits the host selects and the runtime enforces.</summary>
public sealed record LlmAgentHostRuntimePolicy : LlmAgentRuntimePolicy
{
    /// <inheritdoc />
    public override LlmAgentRuntimePolicyKind Kind => LlmAgentRuntimePolicyKind.HostControlled;

    /// <summary>Every filesystem root the provider-owned runtime may write beneath.</summary>
    public IReadOnlyList<string> WritableRoots { get; init; } = [];

    /// <summary>Whether provider-owned commands may use the network.</summary>
    public bool NetworkAllowed { get; init; } = true;

    /// <summary>Filesystem isolation the runtime applies.</summary>
    public LlmAgentSandboxMode Sandbox { get; init; } = LlmAgentSandboxMode.WorkspaceWrite;

    /// <summary>When the runtime asks the host before privileged work.</summary>
    public LlmAgentApprovalPolicy ApprovalPolicy { get; init; } = LlmAgentApprovalPolicy.OnRequest;

    /// <summary>
    /// Lets the runtime start subagents and output monitors of its own. Off by default. Only a runtime whose
    /// <see cref="LlmAgentCapabilities.ResourceObservation"/> is Supported accepts it.
    /// No runtime in the build reads this value; it stays as a host-visible stored setting for the runtimes that read it when they return to the build.
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
    /// <inheritdoc />
    public override LlmAgentRuntimePolicyKind Kind => LlmAgentRuntimePolicyKind.ProviderConfigured;
}

/// <summary>
/// Settings only a provider-owned runtime reads: where it works, whether its conversation persists, and
/// the policy it runs under. A runtime that does not read them accepts and ignores them.
/// </summary>
public sealed record LlmAgentNativeRuntimeOptions
{
    /// <summary>The directory the runtime works in.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>When true, the runtime does not persist its native conversation.</summary>
    public bool Ephemeral { get; init; }

    /// <summary>
    /// The execution policy. Defaults to host-controlled limits. A runtime rejects a variant missing from
    /// its <see cref="LlmAgentCapabilities.SupportedRuntimePolicies"/> before any effect.
    /// </summary>
    public LlmAgentRuntimePolicy Policy { get; init; } = new LlmAgentHostRuntimePolicy();
}

/// <summary>Settings for starting a new agent session.</summary>
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

    /// <summary>The conversation identity to use; null assigns a new one.</summary>
    public string? ThreadId { get; init; }

    /// <summary>The root session identity shared by a thread and its forks; null uses the thread identity.</summary>
    public string? SessionId { get; init; }

    /// <summary>Null selects the defaults. Each adapter reads the members it uses.</summary>
    public LlmAgentExecutionOptions? ExecutionOptions { get; init; }

    /// <summary>Read by provider-owned runtimes; a managed runtime accepts and ignores it.</summary>
    public LlmAgentNativeRuntimeOptions? Runtime { get; init; }

    /// <summary>The model the session runs turns with.</summary>
    public required string ModelId { get; init; }

    /// <summary>System instructions applied to every turn.</summary>
    public string? Instructions { get; init; }

    /// <summary>Host tools the model may call.</summary>
    public IReadOnlyList<LlmAgentClientToolDefinition> Tools { get; init; } = [];

    /// <summary>Borrowed runtime services. The session releases references after admitted work drains.</summary>
    [JsonIgnore]
    public LlmAgentCallerServices? CallerServices { get; init; }

    /// <summary>Provider-hosted tools the model may use.</summary>
    public IReadOnlyList<LlmHostedTool> HostedTools { get; init; } = [];

    /// <summary>Cap on provider-side hosted tool iterations per turn; null leaves the provider default.</summary>
    public int? MaxHostedTurns { get; init; }

    /// <summary>Runs host tool calls when <see cref="ToolHandling"/> is <see cref="LlmAgentToolHandling.Execute"/>.</summary>
    public LlmAgentToolHandler? ToolHandler { get; init; }

    /// <summary>Execute runs the handler; ReturnRequests ends the turn with the calls unrun.</summary>
    public LlmAgentToolHandling ToolHandling { get; init; } = LlmAgentToolHandling.Execute;

    /// <summary>Folders that each hold a SKILL.md. The model can load any of them with the load_skill tool.</summary>
    public IReadOnlyList<string> SkillFolders { get; init; } = [];

    /// <summary>Models tried in order when the session model is overloaded or unavailable for a turn.</summary>
    public IReadOnlyList<string> FallbackModelIds { get; init; } = [];

    /// <summary>Output Connect retains per resource when the runtime keeps none; the earliest cursor advances past it. No runtime in the build reads this value; it stays as a host-visible stored setting for the runtimes that read it when they return to the build.</summary>
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

/// <summary>Settings for resuming an existing agent conversation.</summary>
public sealed record LlmAgentResumeOptions
{
    /// <summary>The model for the resumed session; null leaves the choice to the runtime.</summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// The caller's history of the conversation. A runtime that keeps its own history ignores it; a runtime that
    /// measures and compacts the history itself starts from it, and continues from it when it cannot resume natively.
    /// </summary>
    public LlmAgentContext? Context { get; init; }

    /// <summary>Null selects the defaults. Each adapter reads the members it uses.</summary>
    public LlmAgentExecutionOptions? ExecutionOptions { get; init; }

    /// <summary>Execute runs the handler; ReturnRequests ends the turn with the calls unrun.</summary>
    public LlmAgentToolHandling ToolHandling { get; init; } = LlmAgentToolHandling.Execute;

    /// <summary>Folders that each hold a SKILL.md. The model can load any of them with the load_skill tool.</summary>
    public IReadOnlyList<string> SkillFolders { get; init; } = [];

    /// <summary>Models tried in order when the session model is overloaded or unavailable for a turn.</summary>
    public IReadOnlyList<string> FallbackModelIds { get; init; } = [];

    /// <summary>System instructions applied to every turn after resuming.</summary>
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

    /// <summary>Provider-hosted tools the model may use after resuming.</summary>
    public IReadOnlyList<LlmHostedTool> HostedTools { get; init; } = [];

    /// <summary>Cap on provider-side hosted tool iterations per turn; null leaves the provider default.</summary>
    public int? MaxHostedTurns { get; init; }

    /// <summary>Runs host tool calls made after resuming.</summary>
    public LlmAgentToolHandler? ToolHandler { get; init; }

    /// <summary>Output Connect retains per resource when the runtime keeps none; the earliest cursor advances past it. No runtime in the build reads this value; it stays as a host-visible stored setting for the runtimes that read it when they return to the build.</summary>
    public long ResourceOutputRetentionBytes { get; init; } = 1_048_576;
}

/// <summary>Input and per-turn overrides for one agent turn.</summary>
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

    /// <summary>The model for this turn only; null uses the session model.</summary>
    public string? ModelId { get; init; }

    /// <summary>The requested reasoning level; null leaves the provider's default in place.</summary>
    public Darkspyre.Inference.Common.LLM.LlmReasoningLevel? ReasoningLevel { get; init; }

    /// <summary>The requested tier; null is Auto.</summary>
    public LlmServiceTier? ServiceTier { get; init; }

    /// <summary>
    /// The number of agents a multi-agent model runs for this turn; null leaves the provider's default in place. The
    /// nearest count the model offers is sent; a model that offers none gets none.
    /// </summary>
    public int? AgentCount { get; init; }

    /// <summary>JSON Schema the final answer must match; null requests free-form output.</summary>
    public JsonElement? OutputSchema { get; init; }

    /// <summary>Sampling temperature. A value the selected model cannot use is not sent.</summary>
    public double? Temperature { get; init; }

    /// <summary>Output token cap for each model step. A value the selected route cannot enforce is not sent.</summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>Tool choice for the turn's first model step; later steps use Auto. Not sent without tools.</summary>
    public LlmToolChoice? ToolChoice { get; init; }
}

/// <summary>The outcome class of a finished turn.</summary>
public enum LlmAgentTurnStatus
{
    /// <summary>The turn finished normally.</summary>
    Completed,
    /// <summary>The turn was interrupted.</summary>
    Interrupted,
    /// <summary>The turn ended with an error.</summary>
    Failed,
    /// <summary>Terminal output is available but its natural completion or truncation is unknown.</summary>
    Unknown
}

/// <summary>Base type for the events an agent session reports.</summary>
/// <param name="ThreadId">The conversation the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to, or null outside a turn.</param>
public abstract record LlmAgentEvent(string ThreadId, string? TurnId)
{
    /// <summary>The conversation the event belongs to.</summary>
    public virtual string ThreadId { get; init; } = ThreadId;
    /// <summary>The turn the event belongs to, or null outside a turn.</summary>
    public virtual string? TurnId { get; init; } = TurnId;
}

/// <summary>A turn began.</summary>
/// <param name="ThreadId">The conversation the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to.</param>
public sealed record LlmAgentTurnStartedEvent(string ThreadId, string TurnId)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>A fragment of assistant message text.</summary>
/// <param name="ThreadId">The conversation the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to.</param>
/// <param name="ItemId">The message the text belongs to.</param>
/// <param name="Delta">The new text.</param>
public sealed record LlmAgentTextDeltaEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Delta)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>Replaces the previous partial structured object for this item.</summary>
/// <param name="ItemId">The item the structured output belongs to.</param>
/// <param name="Content">The partial structured object so far.</param>
/// <param name="ThreadId">The thread the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to, or null outside a turn.</param>
public sealed record LlmAgentStructuredOutputSnapshotEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    JsonElement Content)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>A fragment of model reasoning text.</summary>
/// <param name="ThreadId">The conversation the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to.</param>
/// <param name="ItemId">The reasoning item the text belongs to.</param>
/// <param name="Delta">The new text.</param>
/// <param name="IsSummary">True when the text is a reasoning summary instead of raw reasoning.</param>
public sealed record LlmAgentReasoningDeltaEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Delta,
    bool IsSummary)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>An assistant message finished.</summary>
/// <param name="ThreadId">The conversation the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to.</param>
/// <param name="ItemId">The completed message.</param>
/// <param name="Text">The full message text.</param>
public sealed record LlmAgentMessageCompletedEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Text)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>
/// A tool call observed during a turn. <see cref="Output"/> is the result the runtime reported for it, or
/// null when none was reported. It is an observation only: a tool the runtime owns never reaches a host
/// handler through this event.
/// </summary>
/// <param name="ItemId">The identity of the tool call.</param>
/// <param name="Namespace">The namespace of the tool, such as its server name, or null when it has none.</param>
/// <param name="Tool">The name of the tool.</param>
/// <param name="Arguments">The call arguments.</param>
/// <param name="Success">True when the call succeeded, false when it failed, or null while it runs.</param>
/// <param name="Status">The call status the runtime reports, such as <c>inProgress</c>, <c>completed</c> or <c>failed</c>.</param>
/// <param name="Output">The result the runtime reported for the call, or null when none was reported.</param>
/// <param name="ThreadId">The thread the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to, or null outside a turn.</param>
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
    /// <summary>The turn identity, which is never null or blank.</summary>
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
/// <param name="ItemId">The item the progress belongs to.</param>
/// <param name="Activity">The progress text.</param>
/// <param name="ThreadId">The thread the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to, or null outside a turn.</param>
public sealed record LlmAgentActivityEvent(
    string ThreadId,
    string TurnId,
    string ItemId,
    string Activity)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>A non-fatal warning reported during a turn.</summary>
/// <param name="ThreadId">The conversation the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to.</param>
/// <param name="Message">The warning text.</param>
public sealed record LlmAgentWarningEvent(
    string ThreadId,
    string TurnId,
    string Message)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
    [AllowNull]
    public override string TurnId { get => base.TurnId!; init => base.TurnId = HostedEventFacts.Identity(value); }
}

/// <summary>A turn ended.</summary>
/// <param name="ThreadId">The conversation the event belongs to.</param>
/// <param name="TurnId">The turn the event belongs to.</param>
/// <param name="Reason">Why the turn ended.</param>
/// <param name="Error">The failure text, or null when the turn did not fail.</param>
public sealed record LlmAgentTurnCompletedEvent(
    string ThreadId,
    string TurnId,
    LlmAgentCompletionReason Reason,
    string? Error)
    : LlmAgentEvent(ThreadId, HostedEventFacts.Identity(TurnId))
{
    /// <summary>The turn identity, which is never null or blank.</summary>
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

/// <summary>
/// An agent client that resumes a conversation by its thread ID. <see cref="LlmAgentProviderService.ResumeSessionAsync"/>
/// routes a resume to the client registered for the connection's transport and refuses it for a client without this
/// interface. Each client decides how it continues the conversation.
/// </summary>
internal interface ILlmAgentResumeClient : ILlmAgentClient
{
    Task<ILlmAgentSession> ResumeSessionAsync(
        string connectionId,
        LlmConnection connection,
        string threadId,
        LlmAgentResumeOptions? options = null,
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

    /// <summary>The conversation this session runs.</summary>
    string ThreadId { get; }

    /// <summary>Root session identifier shared by a thread and its forks.</summary>
    string SessionId { get; }

    /// <summary>Returns what this session's runtime and model support.</summary>
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

    /// <summary>Resumes a paused turn with replacement context and streams its events.</summary>
    IAsyncEnumerable<LlmAgentEvent> ContinueTurnAsync(
        LlmAgentTurnContinuation continuation,
        CancellationToken cancellationToken = default);

    /// <summary>Runs one turn and streams its events.</summary>
    IAsyncEnumerable<LlmAgentEvent> RunTurnAsync(
        LlmAgentTurnRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Adds input to the running turn.</summary>
    Task SteerAsync(
        string turnId,
        IReadOnlyList<LlmAgentContent> input,
        CancellationToken cancellationToken = default);

    /// <summary>Interrupts the running turn.</summary>
    Task InterruptAsync(string turnId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Session-lifetime events outside host turns: resource changes, output hints and provider-started turns.
    /// One reader at a time; the sequence ends when the session is disposed.
    /// </summary>
    IAsyncEnumerable<LlmAgentEvent> ObserveAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the background resources the runtime holds.</summary>
    Task<IReadOnlyList<LlmAgentResource>> GetResourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>Nondestructive read. A null cursor reads from the earliest retained output.</summary>
    Task<LlmAgentResourceOutput> ReadResourceOutputAsync(
        string resourceId,
        string? cursor,
        CancellationToken cancellationToken = default);

    /// <summary>Asks the runtime to stop a resource and returns the stop outcome.</summary>
    Task<LlmAgentResourceStopStatus> StopResourceAsync(string resourceId, CancellationToken cancellationToken = default);
}
