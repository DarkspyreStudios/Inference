using System.Collections.Frozen;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentRuntimeProfile
{
    public required string Id { get; init => field = CallerData.Id(value); }
    public required string DeclaredRuntimeVersion { get; init => field = CallerData.Id(value); }
    public string? DeclaredAdapterVersion { get; init => field = CallerData.OptionalId(value); }
    public IReadOnlyList<string> Arguments { get; init => field = CallerData.List(value); } = [];
    public IReadOnlySet<string> EnvironmentAllowlist { get; init => field = RuntimeAuthData.Names(value); } = FrozenSet<string>.Empty;
    public override string ToString() => "LlmAgentRuntimeProfile { <configuration> }";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAgentIsolatedRuntimeState), "isolated")]
[JsonDerivedType(typeof(LlmAgentAdoptedRuntimeState), "adopted")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAgentRuntimeStateConfiguration
{
    private protected LlmAgentRuntimeStateConfiguration() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentIsolatedRuntimeState(string RootDirectory, string? ActivatedDirectory = null) : LlmAgentRuntimeStateConfiguration
{
    [JsonRequired] public string RootDirectory { get; init => field = CallerData.Path(value); } = CallerData.Path(RootDirectory);
    public string? ActivatedDirectory { get; init => field = CallerData.OptionalPath(value); } = CallerData.OptionalPath(ActivatedDirectory);
    public override string ToString() => "LlmAgentIsolatedRuntimeState { <configuration> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentAdoptedRuntimeState(string Directory) : LlmAgentRuntimeStateConfiguration
{
    [JsonRequired] public string Directory { get; init => field = CallerData.Path(value); } = CallerData.Path(Directory);
    public override string ToString() => "LlmAgentAdoptedRuntimeState { <configuration> }";
}

/// <summary>Transient explicit values, not persisted configuration or an instruction to inherit the environment.</summary>
public sealed record LlmAgentRuntimeLaunchInputs
{
    public IReadOnlyList<LlmAgentEnvironmentVariable> Environment { get; init => field = RuntimeAuthData.Environment(value); } = [];
    public override string ToString() => "LlmAgentRuntimeLaunchInputs { <redacted> }";
}

internal static class RuntimeAuthData
{
    internal static FrozenSet<string> Names(IReadOnlySet<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Select(Name).ToFrozenSet(StringComparer.Ordinal);
    }
    private static string Name(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('=') || name.Contains('\0')) throw new ArgumentException("An environment name is malformed.", nameof(name));
        return name;
    }
    internal static IReadOnlyList<LlmAgentEnvironmentVariable> Environment(IReadOnlyList<LlmAgentEnvironmentVariable> values)
    {
        var copy = CallerData.List(values);
        var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (LlmAgentEnvironmentVariable value in copy)
        {
            if (!names.Add(Name(value.Name)) || value.Value.Contains('\0'))
                throw new ArgumentException("Explicit environment entries must have distinct names and valid values.", nameof(values));
        }
        return copy;
    }
    internal static LlmTransport Transport(LlmTransport value) => value is LlmTransport.GitHubCopilot or LlmTransport.AgentClientProtocol
        ? value : throw new ArgumentOutOfRangeException(nameof(value));
    internal static Guid Attempt(Guid value) => value != Guid.Empty ? value : throw new ArgumentException("A login attempt identity is required.", nameof(value));
    internal static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
}
