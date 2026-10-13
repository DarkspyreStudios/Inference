using System.Text.Json.Serialization;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>How a Copilot connection reaches its runtime.</summary>
public enum LlmCopilotEndpointMode
{
    /// <summary>The connection launches and owns the runtime executable.</summary>
    OwnedExecutable = 0,
    /// <summary>The connection attaches to a runtime server already listening on loopback.</summary>
    ExistingLoopbackServer = 1
}

/// <summary>Non-secret configuration of a GitHub Copilot agent connection.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmCopilotConfiguration
{
    /// <summary>The runtime profile that describes the Copilot runtime and how it launches.</summary>
    public required LlmAgentRuntimeProfile Profile { get; init => field = CallerData.Required(value); }
    /// <summary>Whether the connection owns the runtime executable or attaches to an existing loopback server.</summary>
    public LlmCopilotEndpointMode EndpointMode { get; init => field = RuntimeAuthData.Defined(value); }
    /// <summary>Where the runtime keeps its state, or null for no state configuration.</summary>
    public LlmAgentRuntimeStateConfiguration? State { get; init; }
    /// <summary>How the connection authenticates to Copilot.</summary>
    public required LlmCopilotAuthenticationConfiguration Authentication { get; init => field = CallerData.Required(value); }
    /// <summary>The Copilot SDK version the connection requires, or null when any version is accepted.</summary>
    public string? RequiredSdkVersion { get; init => field = CallerData.OptionalId(value); }
    /// <summary>Returns a fixed placeholder that hides the configuration values.</summary>
    public override string ToString() => "LlmCopilotConfiguration { <configuration> }";
}

/// <summary>The authentication method of a Copilot connection, serialized with a <c>type</c> discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmCopilotExplicitGitHubToken), "explicit-github-token")]
[JsonDerivedType(typeof(LlmCopilotRuntimeAccount), "runtime-account")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record LlmCopilotAuthenticationConfiguration
{
    private protected LlmCopilotAuthenticationConfiguration() { }
}
/// <summary>Authentication with a GitHub token held in the connection's protected credential.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmCopilotExplicitGitHubToken() : LlmCopilotAuthenticationConfiguration
{
    /// <summary>Returns a fixed placeholder that hides the configuration values.</summary>
    public override string ToString() => "LlmCopilotExplicitGitHubToken { <configuration> }";
}
/// <summary>Authentication with the account the Copilot runtime itself is signed in to.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmCopilotRuntimeAccount() : LlmCopilotAuthenticationConfiguration
{
    /// <summary>Returns a fixed placeholder that hides the configuration values.</summary>
    public override string ToString() => "LlmCopilotRuntimeAccount { <configuration> }";
}

/// <summary>Protected-store token data. This root is not safe for logs or context.</summary>
public sealed record LlmCopilotCredentials
{
    /// <summary>The Copilot access token.</summary>
    public required string AccessToken { get; init => field = CallerData.Required(value); }
    /// <summary>When the access token expires, or null when the expiry is unknown.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>Returns a fixed placeholder that hides the token.</summary>
    public override string ToString() => "LlmCopilotCredentials { <redacted> }";
}

/// <summary>Distinct protected transport purpose; declaration does not enable existing-server mode.</summary>
public sealed record LlmCopilotRuntimeConnectionCredential
{
    /// <summary>The token that authenticates the connection to the Copilot runtime transport.</summary>
    public required string ConnectionToken { get; init => field = CallerData.Required(value); }
    /// <summary>Returns a fixed placeholder that hides the token.</summary>
    public override string ToString() => "LlmCopilotRuntimeConnectionCredential { <redacted> }";
}
