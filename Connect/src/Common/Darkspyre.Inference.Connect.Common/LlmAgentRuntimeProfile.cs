using System.Collections.Frozen;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Identifies an agent runtime and how to launch it.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentRuntimeProfile
{
    /// <summary>The runtime identifier.</summary>
    public required string Id { get; init => field = CallerData.Id(value); }
    /// <summary>The runtime version the profile declares.</summary>
    public required string DeclaredRuntimeVersion { get; init => field = CallerData.Id(value); }
    /// <summary>The adapter version the profile declares, or null when the runtime has no adapter.</summary>
    public string? DeclaredAdapterVersion { get; init => field = CallerData.OptionalId(value); }
    /// <summary>The command-line arguments passed to the runtime. The list is copied.</summary>
    public IReadOnlyList<string> Arguments { get; init => field = CallerData.List(value); } = [];
    /// <summary>The names of the environment variables the runtime may receive.</summary>
    public IReadOnlySet<string> EnvironmentAllowlist { get; init => field = RuntimeAuthData.Names(value); } = FrozenSet<string>.Empty;
    /// <summary>Returns a fixed placeholder so configuration values never reach logs.</summary>
    public override string ToString() => "LlmAgentRuntimeProfile { <configuration> }";
}

/// <summary>Base type for where an agent runtime keeps its state, serialized with a <c>type</c> discriminator of <c>isolated</c> or <c>adopted</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAgentIsolatedRuntimeState), "isolated")]
[JsonDerivedType(typeof(LlmAgentAdoptedRuntimeState), "adopted")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAgentRuntimeStateConfiguration
{
    private protected LlmAgentRuntimeStateConfiguration() { }
}

/// <summary>Runtime state kept in a directory the app owns.</summary>
/// <param name="RootDirectory">The directory that holds the runtime state.</param>
/// <param name="ActivatedDirectory">The activated state directory, or null when none is set.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentIsolatedRuntimeState(string RootDirectory, string? ActivatedDirectory = null) : LlmAgentRuntimeStateConfiguration
{
    /// <summary>The directory that holds the runtime state.</summary>
    [JsonRequired] public string RootDirectory { get; init => field = CallerData.Path(value); } = CallerData.Path(RootDirectory);
    /// <summary>The activated state directory, or null when none is set.</summary>
    public string? ActivatedDirectory { get; init => field = CallerData.OptionalPath(value); } = CallerData.OptionalPath(ActivatedDirectory);
    /// <summary>Returns a fixed placeholder so configuration values never reach logs.</summary>
    public override string ToString() => "LlmAgentIsolatedRuntimeState { <configuration> }";
}

/// <summary>Runtime state kept in an existing directory the runtime already uses.</summary>
/// <param name="Directory">The existing state directory.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAgentAdoptedRuntimeState(string Directory) : LlmAgentRuntimeStateConfiguration
{
    /// <summary>The existing state directory.</summary>
    [JsonRequired] public string Directory { get; init => field = CallerData.Path(value); } = CallerData.Path(Directory);
    /// <summary>Returns a fixed placeholder so configuration values never reach logs.</summary>
    public override string ToString() => "LlmAgentAdoptedRuntimeState { <configuration> }";
}

/// <summary>Transient explicit values, not persisted configuration or an instruction to inherit the environment.</summary>
public sealed record LlmAgentRuntimeLaunchInputs
{
    /// <summary>The environment variables passed to the runtime at launch. Names must be distinct and values must not contain null characters.</summary>
    public IReadOnlyList<LlmAgentEnvironmentVariable> Environment { get; init => field = RuntimeAuthData.Environment(value); } = [];
    /// <summary>Returns a fixed placeholder so environment values never reach logs.</summary>
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
