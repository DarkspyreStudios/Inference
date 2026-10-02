using System.Text.Json.Serialization;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

public enum LlmCopilotEndpointMode { OwnedExecutable = 0, ExistingLoopbackServer = 1 }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmCopilotConfiguration
{
    public required LlmAgentRuntimeProfile Profile { get; init => field = CallerData.Required(value); }
    public LlmCopilotEndpointMode EndpointMode { get; init => field = RuntimeAuthData.Defined(value); }
    public LlmAgentRuntimeStateConfiguration? State { get; init; }
    public required LlmCopilotAuthenticationConfiguration Authentication { get; init => field = CallerData.Required(value); }
    public string? RequiredSdkVersion { get; init => field = CallerData.OptionalId(value); }
    public override string ToString() => "LlmCopilotConfiguration { <configuration> }";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmCopilotExplicitGitHubToken), "explicit-github-token")]
[JsonDerivedType(typeof(LlmCopilotRuntimeAccount), "runtime-account")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmCopilotAuthenticationConfiguration
{
    private protected LlmCopilotAuthenticationConfiguration() { }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmCopilotExplicitGitHubToken() : LlmCopilotAuthenticationConfiguration
{
    public override string ToString() => "LlmCopilotExplicitGitHubToken { <configuration> }";
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmCopilotRuntimeAccount() : LlmCopilotAuthenticationConfiguration
{
    public override string ToString() => "LlmCopilotRuntimeAccount { <configuration> }";
}

/// <summary>Protected-store token data. This root is not safe for logs or context.</summary>
public sealed record LlmCopilotCredentials
{
    public required string AccessToken { get; init => field = CallerData.Required(value); }
    public DateTimeOffset? ExpiresAt { get; init; }
    public override string ToString() => "LlmCopilotCredentials { <redacted> }";
}

/// <summary>Distinct protected transport purpose; declaration does not enable existing-server mode.</summary>
public sealed record LlmCopilotRuntimeConnectionCredential
{
    public required string ConnectionToken { get; init => field = CallerData.Required(value); }
    public override string ToString() => "LlmCopilotRuntimeConnectionCredential { <redacted> }";
}
