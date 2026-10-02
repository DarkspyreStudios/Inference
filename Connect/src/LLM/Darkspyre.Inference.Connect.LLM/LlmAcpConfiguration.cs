using System.Text.Json.Serialization;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAcpConfiguration
{
    public required LlmAgentRuntimeProfile Profile { get; init => field = CallerData.Required(value); }
    public required LlmAgentRuntimeStateConfiguration State { get; init => field = CallerData.Required(value); }
    public required LlmAcpAuthenticationConfiguration Authentication { get; init => field = CallerData.Required(value); }
    public LlmAcpToolRelayConfiguration? ToolRelay { get; init; }
    public uint ProtocolVersion { get; init; } = 1;
    public override string ToString() => "LlmAcpConfiguration { <configuration> }";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAcpToolRelayConfiguration(string ManagedHostExecutable)
{
    [JsonRequired] public string ManagedHostExecutable { get; init => field = CallerData.Path(value); } = CallerData.Path(ManagedHostExecutable);
    public override string ToString() => "LlmAcpToolRelayConfiguration { <configuration> }";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAcpExternalAuthentication), "external")]
[JsonDerivedType(typeof(LlmAcpAgentAuthentication), "agent")]
[JsonDerivedType(typeof(LlmAcpTerminalAuthentication), "terminal")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmAcpAuthenticationConfiguration
{
    private protected LlmAcpAuthenticationConfiguration() { }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAcpExternalAuthentication() : LlmAcpAuthenticationConfiguration
{
    public override string ToString() => "LlmAcpExternalAuthentication { <configuration> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAcpAgentAuthentication(string MethodId) : LlmAcpAuthenticationConfiguration
{
    [JsonRequired] public string MethodId { get; init => field = CallerData.Id(value); } = CallerData.Id(MethodId);
    public override string ToString() => "LlmAcpAgentAuthentication { <configuration> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAcpTerminalAuthentication(string MethodId) : LlmAcpAuthenticationConfiguration
{
    [JsonRequired] public string MethodId { get; init => field = CallerData.Id(value); } = CallerData.Id(MethodId);
    public override string ToString() => "LlmAcpTerminalAuthentication { <configuration> }";
}
public enum LlmAcpAuthenticationFlow { Agent = 0, Terminal = 1 }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAcpAuthenticationMethod
{
    public required string Id { get; init => field = CallerData.Id(value); }
    public required string Name { get; init => field = CallerData.Required(value); }
    public string? Description { get; init; }
    [JsonRequired] public LlmAcpAuthenticationFlow Flow { get; init => field = RuntimeAuthData.Defined(value); }
    public override string ToString() => "LlmAcpAuthenticationMethod { <redacted> }";
}
