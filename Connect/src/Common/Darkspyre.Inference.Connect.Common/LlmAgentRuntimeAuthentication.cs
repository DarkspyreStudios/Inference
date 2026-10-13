using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The configuration, transport, runtime profile and working directory one runtime login applies to.</summary>
/// <param name="Identity">The setup configuration the login belongs to.</param>
/// <param name="Transport">The agent transport that signs in.</param>
/// <param name="ProfileId">The id of the runtime profile that signs in.</param>
/// <param name="WorkingDirectory">The directory the login runs in.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentRuntimeLoginScope(SetupConfigurationIdentity Identity, LlmTransport Transport, string ProfileId, string WorkingDirectory)
{
    /// <summary>The setup configuration the login belongs to.</summary>
    [JsonRequired] public SetupConfigurationIdentity Identity { get; init => field = CallerData.Required(value); } = CallerData.Required(Identity);
    /// <summary>The agent transport that signs in.</summary>
    [JsonRequired] public LlmTransport Transport { get; init => field = RuntimeAuthData.Transport(value); } = RuntimeAuthData.Transport(Transport);
    /// <summary>The id of the runtime profile.</summary>
    [JsonRequired] public string ProfileId { get; init => field = CallerData.Id(value); } = CallerData.Id(ProfileId);
    /// <summary>The directory the login runs in.</summary>
    [JsonRequired] public string WorkingDirectory { get; init => field = CallerData.Path(value); } = CallerData.Path(WorkingDirectory);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentRuntimeLoginScope { <scoped-state> }";
}

/// <summary>Live login invocation; never a portable metadata root.</summary>
public sealed record LlmAgentRuntimeLoginTerminalRequest(LlmAgentRuntimeLoginScope Scope, string Executable,
    IReadOnlyList<string> Arguments, IReadOnlyList<LlmAgentEnvironmentVariable> Environment)
{
    /// <summary>The configuration, transport, profile and directory the login applies to.</summary>
    public LlmAgentRuntimeLoginScope Scope { get; init => field = CallerData.Required(value); } = CallerData.Required(Scope);
    /// <summary>The path of the runtime executable that performs the login.</summary>
    public string Executable { get; init => field = CallerData.Path(value); } = CallerData.Path(Executable);
    /// <summary>The command-line arguments passed to the executable.</summary>
    public IReadOnlyList<string> Arguments { get; init => field = CallerData.List(value); } = CallerData.List(Arguments);
    /// <summary>The environment variables the login process receives.</summary>
    public IReadOnlyList<LlmAgentEnvironmentVariable> Environment { get; init => field = RuntimeAuthData.Environment(value); } = RuntimeAuthData.Environment(Environment);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentRuntimeLoginTerminalRequest { <redacted> }";
}

/// <summary>Creates the caller-owned terminal a runtime login runs in.</summary>
public interface ILlmAgentRuntimeLoginTerminalFactory
{
    /// <summary>Starts the login command described by <paramref name="request"/> in a new terminal and returns that terminal.</summary>
    Task<ILlmAgentTerminal> CreateAsync(LlmAgentRuntimeLoginTerminalRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Caller services a runtime login borrows to show prompts and run login commands.</summary>
public sealed record LlmAgentRuntimeLoginServices
{
    /// <summary>The prompt that shows a device code to the user, or null when none is available.</summary>
    public ILlmAuthPrompt? DevicePrompt { get; init; }
    /// <summary>The factory that runs login commands in a terminal, or null when none is available.</summary>
    public ILlmAgentRuntimeLoginTerminalFactory? Terminals { get; init; }
    /// <inheritdoc />
    public override string ToString() => "LlmAgentRuntimeLoginServices { <borrowed> }";
}

/// <summary>An observation of a runtime profile's sign-in state.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentRuntimeAuthenticationSnapshot
{
    /// <summary>The agent transport that was inspected.</summary>
    public required LlmTransport Transport { get; init => field = RuntimeAuthData.Transport(value); }
    /// <summary>The id of the runtime profile that was inspected.</summary>
    public required string ProfileId { get; init => field = CallerData.Id(value); }
    /// <summary>True when the runtime is signed in, false when it is not, or null when the state is unknown.</summary>
    public bool? Authenticated { get; init; }
    /// <summary>The account the runtime is signed in as, or null when not known.</summary>
    public string? AccountId { get; init; }
    /// <summary>The ACP authentication methods the runtime offers; empty when it offers none.</summary>
    public IReadOnlyList<LlmAcpAuthenticationMethod> AcpMethods { get; init => field = CallerData.List(value); } = [];
    /// <summary>Why the sign-in state could not be observed, or null when it was observed.</summary>
    public string? UnavailableReason { get; init; }
    /// <inheritdoc />
    public override string ToString() => "LlmAgentRuntimeAuthenticationSnapshot { <observation> }";
}

/// <summary>The inputs for one runtime sign-in.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentRuntimeLoginOptions
{
    /// <summary>The directory the login runs in.</summary>
    public required string WorkingDirectory { get; init => field = CallerData.Path(value); }
    /// <summary>The caller services the login borrows, or null when it has none.</summary>
    public LlmAgentRuntimeLoginServices? Services { get; init; }
    /// <inheritdoc />
    public override string ToString() => "LlmAgentRuntimeLoginOptions { <runtime-inputs> }";
}

/// <summary>The receipt for login state a sign-in staged and that a configuration commit has not yet activated.</summary>
/// <param name="AttemptId">The id of the sign-in attempt that staged the state.</param>
/// <param name="Identity">The setup configuration the staged state belongs to.</param>
/// <param name="ProfileId">The id of the runtime profile that signed in.</param>
/// <param name="RootDirectory">The root directory of the profile's isolated runtime state.</param>
/// <param name="Directory">The directory that holds the staged login state.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentStagedLoginState(Guid AttemptId, SetupConfigurationIdentity Identity, string ProfileId, string RootDirectory, string Directory)
{
    /// <summary>The id of the sign-in attempt that staged the state.</summary>
    [JsonRequired] public Guid AttemptId { get; init => field = RuntimeAuthData.Attempt(value); } = RuntimeAuthData.Attempt(AttemptId);
    /// <summary>The setup configuration the login belongs to.</summary>
    [JsonRequired] public SetupConfigurationIdentity Identity { get; init => field = CallerData.Required(value); } = CallerData.Required(Identity);
    /// <summary>The id of the runtime profile.</summary>
    [JsonRequired] public string ProfileId { get; init => field = CallerData.Id(value); } = CallerData.Id(ProfileId);
    /// <summary>The root directory of the profile's isolated runtime state.</summary>
    [JsonRequired] public string RootDirectory { get; init => field = CallerData.Path(value); } = CallerData.Path(RootDirectory);
    /// <summary>The directory that holds the staged login state.</summary>
    [JsonRequired] public string Directory { get; init => field = CallerData.Path(value); } = CallerData.Path(Directory);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentStagedLoginState { <scoped-state> }";
}

/// <summary>The result of a runtime sign-in.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentRuntimeLoginResult
{
    /// <summary>The sign-in state observed after the login.</summary>
    public required LlmAgentRuntimeAuthenticationSnapshot Observation { get; init => field = CallerData.Required(value); }
    /// <summary>True when the login staged state that takes effect only after a configuration commit.</summary>
    [JsonRequired] public bool RequiresConfigurationCommit { get; init; }
    /// <summary>The receipt for the staged login state, or null when the login staged none.</summary>
    public LlmAgentStagedLoginState? StagedState { get; init; }
    /// <inheritdoc />
    public override string ToString() => "LlmAgentRuntimeLoginResult { <observation> }";
}

/// <summary>What has become of staged login state. Members: Unactivated, Activated, Uncertain, Abandoned.</summary>
public enum LlmAgentStagedStateDisposition
{
    /// <summary>The staged state exists and no configuration commit has put it into effect.</summary>
    Unactivated = 0,
    /// <summary>A configuration commit has put the staged state into effect.</summary>
    Activated = 1,
    /// <summary>The runtime cannot tell whether the staged state took effect.</summary>
    Uncertain = 2,
    /// <summary>The staged state was discarded and never takes effect.</summary>
    Abandoned = 3
}

/// <summary>An observation of what has become of one sign-in attempt's staged login state.</summary>
/// <param name="AttemptId">The id of the sign-in attempt that staged the state.</param>
/// <param name="Disposition">What has become of the staged state.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentStagedStateSnapshot(Guid AttemptId, LlmAgentStagedStateDisposition Disposition)
{
    /// <summary>The id of the sign-in attempt that staged the state.</summary>
    [JsonRequired] public Guid AttemptId { get; init => field = RuntimeAuthData.Attempt(value); } = RuntimeAuthData.Attempt(AttemptId);
    /// <summary>What has become of the staged state.</summary>
    [JsonRequired] public LlmAgentStagedStateDisposition Disposition { get; init => field = RuntimeAuthData.Defined(value); } = RuntimeAuthData.Defined(Disposition);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentStagedStateSnapshot { <observation> }";
}

/// <summary>The result of committing a configuration change together with staged login state.</summary>
/// <param name="Commit">The result of the configuration commit.</param>
/// <param name="State">What became of the staged login state.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentStagedStateCommitResult(SetupCommitResult Commit, LlmAgentStagedStateSnapshot State)
{
    /// <summary>The result of the configuration commit.</summary>
    [JsonRequired] public SetupCommitResult Commit { get; init => field = CallerData.Required(value); } = CallerData.Required(Commit);
    /// <summary>What became of the staged login state.</summary>
    [JsonRequired] public LlmAgentStagedStateSnapshot State { get; init => field = CallerData.Required(value); } = CallerData.Required(State);
    /// <inheritdoc />
    public override string ToString() => "LlmAgentStagedStateCommitResult { <observation> }";
}

/// <summary>One generation-owned login lifecycle; declarations alone grant no receipt authority or adapter support.</summary>
public interface ILlmAgentRuntimeAuthentication
{
    /// <summary>Observes the runtime profile's current sign-in state.</summary>
    Task<LlmAgentRuntimeAuthenticationSnapshot> InspectAsync(CancellationToken cancellationToken = default);
    /// <summary>Signs the runtime profile in and returns the observed state plus any staged login state.</summary>
    Task<LlmAgentRuntimeLoginResult> SignInAsync(LlmAgentRuntimeLoginOptions options, CancellationToken cancellationToken = default);
    /// <summary>Commits <paramref name="changes"/> to <paramref name="target"/> together with the staged login state named by <paramref name="receipt"/>.</summary>
    Task<LlmAgentStagedStateCommitResult> CommitStagedStateAsync(LlmAgentStagedLoginState receipt, LlmSetupChanges changes,
        ISetupCommitTarget<LlmSetupChanges> target, CancellationToken cancellationToken = default);
    /// <summary>Observes what has become of the staged login state named by <paramref name="receipt"/>.</summary>
    Task<LlmAgentStagedStateSnapshot> ReconcileStagedStateAsync(LlmAgentStagedLoginState receipt, CancellationToken cancellationToken = default);
    /// <summary>Discards the staged login state named by <paramref name="receipt"/> and returns its final disposition.</summary>
    Task<LlmAgentStagedStateSnapshot> AbandonStagedStateAsync(LlmAgentStagedLoginState receipt, CancellationToken cancellationToken = default);
}
