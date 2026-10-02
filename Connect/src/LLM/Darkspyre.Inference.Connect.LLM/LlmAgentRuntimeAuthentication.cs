using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentRuntimeLoginScope(SetupConfigurationIdentity Identity, LlmTransport Transport, string ProfileId, string WorkingDirectory)
{
    [JsonRequired] public SetupConfigurationIdentity Identity { get; init => field = CallerData.Required(value); } = CallerData.Required(Identity);
    [JsonRequired] public LlmTransport Transport { get; init => field = RuntimeAuthData.Transport(value); } = RuntimeAuthData.Transport(Transport);
    [JsonRequired] public string ProfileId { get; init => field = CallerData.Id(value); } = CallerData.Id(ProfileId);
    [JsonRequired] public string WorkingDirectory { get; init => field = CallerData.Path(value); } = CallerData.Path(WorkingDirectory);
    public override string ToString() => "LlmAgentRuntimeLoginScope { <scoped-state> }";
}

/// <summary>Live login invocation; never a portable metadata root.</summary>
public sealed record LlmAgentRuntimeLoginTerminalRequest(LlmAgentRuntimeLoginScope Scope, string Executable,
    IReadOnlyList<string> Arguments, IReadOnlyList<LlmAgentEnvironmentVariable> Environment)
{
    public LlmAgentRuntimeLoginScope Scope { get; init => field = CallerData.Required(value); } = CallerData.Required(Scope);
    public string Executable { get; init => field = CallerData.Path(value); } = CallerData.Path(Executable);
    public IReadOnlyList<string> Arguments { get; init => field = CallerData.List(value); } = CallerData.List(Arguments);
    public IReadOnlyList<LlmAgentEnvironmentVariable> Environment { get; init => field = RuntimeAuthData.Environment(value); } = RuntimeAuthData.Environment(Environment);
    public override string ToString() => "LlmAgentRuntimeLoginTerminalRequest { <redacted> }";
}

public interface ILlmAgentRuntimeLoginTerminalFactory
{
    Task<ILlmAgentTerminal> CreateAsync(LlmAgentRuntimeLoginTerminalRequest request, CancellationToken cancellationToken = default);
}

public sealed record LlmAgentRuntimeLoginServices
{
    public ILlmAuthPrompt? DevicePrompt { get; init; }
    public ILlmAgentRuntimeLoginTerminalFactory? Terminals { get; init; }
    public override string ToString() => "LlmAgentRuntimeLoginServices { <borrowed> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentRuntimeAuthenticationSnapshot
{
    public required LlmTransport Transport { get; init => field = RuntimeAuthData.Transport(value); }
    public required string ProfileId { get; init => field = CallerData.Id(value); }
    public bool? Authenticated { get; init; }
    public string? AccountId { get; init; }
    public IReadOnlyList<LlmAcpAuthenticationMethod> AcpMethods { get; init => field = CallerData.List(value); } = [];
    public string? UnavailableReason { get; init; }
    public override string ToString() => "LlmAgentRuntimeAuthenticationSnapshot { <observation> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentRuntimeLoginOptions
{
    public required string WorkingDirectory { get; init => field = CallerData.Path(value); }
    public LlmAgentRuntimeLoginServices? Services { get; init; }
    public override string ToString() => "LlmAgentRuntimeLoginOptions { <runtime-inputs> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentStagedLoginState(Guid AttemptId, SetupConfigurationIdentity Identity, string ProfileId, string RootDirectory, string Directory)
{
    [JsonRequired] public Guid AttemptId { get; init => field = RuntimeAuthData.Attempt(value); } = RuntimeAuthData.Attempt(AttemptId);
    [JsonRequired] public SetupConfigurationIdentity Identity { get; init => field = CallerData.Required(value); } = CallerData.Required(Identity);
    [JsonRequired] public string ProfileId { get; init => field = CallerData.Id(value); } = CallerData.Id(ProfileId);
    [JsonRequired] public string RootDirectory { get; init => field = CallerData.Path(value); } = CallerData.Path(RootDirectory);
    [JsonRequired] public string Directory { get; init => field = CallerData.Path(value); } = CallerData.Path(Directory);
    public override string ToString() => "LlmAgentStagedLoginState { <scoped-state> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentRuntimeLoginResult
{
    public required LlmAgentRuntimeAuthenticationSnapshot Observation { get; init => field = CallerData.Required(value); }
    [JsonRequired] public bool RequiresConfigurationCommit { get; init; }
    public LlmAgentStagedLoginState? StagedState { get; init; }
    public override string ToString() => "LlmAgentRuntimeLoginResult { <observation> }";
}

public enum LlmAgentStagedStateDisposition { Unactivated = 0, Activated = 1, Uncertain = 2, Abandoned = 3 }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentStagedStateSnapshot(Guid AttemptId, LlmAgentStagedStateDisposition Disposition)
{
    [JsonRequired] public Guid AttemptId { get; init => field = RuntimeAuthData.Attempt(value); } = RuntimeAuthData.Attempt(AttemptId);
    [JsonRequired] public LlmAgentStagedStateDisposition Disposition { get; init => field = RuntimeAuthData.Defined(value); } = RuntimeAuthData.Defined(Disposition);
    public override string ToString() => "LlmAgentStagedStateSnapshot { <observation> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentStagedStateCommitResult(SetupCommitResult Commit, LlmAgentStagedStateSnapshot State)
{
    [JsonRequired] public SetupCommitResult Commit { get; init => field = CallerData.Required(value); } = CallerData.Required(Commit);
    [JsonRequired] public LlmAgentStagedStateSnapshot State { get; init => field = CallerData.Required(value); } = CallerData.Required(State);
    public override string ToString() => "LlmAgentStagedStateCommitResult { <observation> }";
}

/// <summary>One generation-owned login lifecycle; declarations alone grant no receipt authority or adapter support.</summary>
public interface ILlmAgentRuntimeAuthentication
{
    Task<LlmAgentRuntimeAuthenticationSnapshot> InspectAsync(CancellationToken cancellationToken = default);
    Task<LlmAgentRuntimeLoginResult> SignInAsync(LlmAgentRuntimeLoginOptions options, CancellationToken cancellationToken = default);
    Task<LlmAgentStagedStateCommitResult> CommitStagedStateAsync(LlmAgentStagedLoginState receipt, LlmSetupChanges changes,
        ISetupCommitTarget<LlmSetupChanges> target, CancellationToken cancellationToken = default);
    Task<LlmAgentStagedStateSnapshot> ReconcileStagedStateAsync(LlmAgentStagedLoginState receipt, CancellationToken cancellationToken = default);
    Task<LlmAgentStagedStateSnapshot> AbandonStagedStateAsync(LlmAgentStagedLoginState receipt, CancellationToken cancellationToken = default);
}
