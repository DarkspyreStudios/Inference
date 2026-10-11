using System.Text.Json.Serialization;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Configuration for running an agent over the Agent Client Protocol (ACP).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAcpConfiguration
{
    /// <summary>The agent runtime to launch.</summary>
    public required LlmAgentRuntimeProfile Profile { get; init => field = CallerData.Required(value); }
    /// <summary>Where the agent runtime keeps its state.</summary>
    public required LlmAgentRuntimeStateConfiguration State { get; init => field = CallerData.Required(value); }
    /// <summary>How the agent authenticates.</summary>
    public required LlmAcpAuthenticationConfiguration Authentication { get; init => field = CallerData.Required(value); }
    /// <summary>The relay that exposes client tools to the agent, or null for none.</summary>
    public LlmAcpToolRelayConfiguration? ToolRelay { get; init; }
    /// <summary>The ACP protocol version to request.</summary>
    public uint ProtocolVersion { get; init; } = 1;
    /// <summary>Returns a fixed placeholder so configuration values never reach logs.</summary>
    public override string ToString() => "LlmAcpConfiguration { <configuration> }";
}

/// <summary>Configuration for the relay that exposes client tools to an ACP agent.</summary>
/// <param name="ManagedHostExecutable">The path of the executable that hosts the relay.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAcpToolRelayConfiguration(string ManagedHostExecutable)
{
    /// <summary>The path of the executable that hosts the relay.</summary>
    [JsonRequired] public string ManagedHostExecutable { get; init => field = CallerData.Path(value); } = CallerData.Path(ManagedHostExecutable);
    /// <summary>Returns a fixed placeholder so configuration values never reach logs.</summary>
    public override string ToString() => "LlmAcpToolRelayConfiguration { <configuration> }";
}

/// <summary>Base type for how an ACP agent authenticates, serialized with a <c>type</c> discriminator of <c>external</c>, <c>agent</c> or <c>terminal</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAcpExternalAuthentication), "external")]
[JsonDerivedType(typeof(LlmAcpAgentAuthentication), "agent")]
[JsonDerivedType(typeof(LlmAcpTerminalAuthentication), "terminal")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAcpAuthenticationConfiguration
{
    private protected LlmAcpAuthenticationConfiguration() { }
}
/// <summary>Authentication handled outside the app, so the agent starts already signed in.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAcpExternalAuthentication() : LlmAcpAuthenticationConfiguration
{
    /// <summary>Returns a fixed placeholder so configuration values never reach logs.</summary>
    public override string ToString() => "LlmAcpExternalAuthentication { <configuration> }";
}
/// <summary>Authentication through an agent-run method that the agent advertises.</summary>
/// <param name="MethodId">The identifier of the agent authentication method.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAcpAgentAuthentication(string MethodId) : LlmAcpAuthenticationConfiguration
{
    /// <summary>The identifier of the agent authentication method.</summary>
    [JsonRequired] public string MethodId { get; init => field = CallerData.Id(value); } = CallerData.Id(MethodId);
    /// <summary>Returns a fixed placeholder so configuration values never reach logs.</summary>
    public override string ToString() => "LlmAcpAgentAuthentication { <configuration> }";
}
/// <summary>Authentication through a method the user completes in a terminal.</summary>
/// <param name="MethodId">The identifier of the agent authentication method.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAcpTerminalAuthentication(string MethodId) : LlmAcpAuthenticationConfiguration
{
    /// <summary>The identifier of the agent authentication method.</summary>
    [JsonRequired] public string MethodId { get; init => field = CallerData.Id(value); } = CallerData.Id(MethodId);
    /// <summary>Returns a fixed placeholder so configuration values never reach logs.</summary>
    public override string ToString() => "LlmAcpTerminalAuthentication { <configuration> }";
}
/// <summary>Where the user completes an ACP authentication method.</summary>
public enum LlmAcpAuthenticationFlow
{
    /// <summary>The agent runs the method itself.</summary>
    Agent = 0,
    /// <summary>The user completes the method in a terminal.</summary>
    Terminal = 1
}

/// <summary>An authentication method an ACP agent advertises.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAcpAuthenticationMethod
{
    /// <summary>The method identifier.</summary>
    public required string Id { get; init => field = CallerData.Id(value); }
    /// <summary>The method name shown to the user.</summary>
    public required string Name { get; init => field = CallerData.Required(value); }
    /// <summary>A description of the method, or null.</summary>
    public string? Description { get; init; }
    /// <summary>Where the user completes the method.</summary>
    [JsonRequired] public LlmAcpAuthenticationFlow Flow { get; init => field = RuntimeAuthData.Defined(value); }
    /// <summary>Returns a fixed placeholder so method details never reach logs.</summary>
    public override string ToString() => "LlmAcpAuthenticationMethod { <redacted> }";
}
