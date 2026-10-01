namespace Darkspyre.Inference.Connect.LLM;

public enum LlmGoogleAuthenticationMode
{
    ApplicationDefault,
    ServiceAccount,
    /// <summary>Global projectless Vertex express mode with a Google Cloud API key. Gemini only; no model listing.</summary>
    ApiKey
}

/// <summary>Non-secret Google Cloud connection selection. Credentials remain in LlmCredential.</summary>
public sealed record LlmVertexConfiguration
{
    public string? ProjectId { get; init; }
    public string? Location { get; init; }
    public LlmGoogleAuthenticationMode AuthenticationMode { get; init; }
}

/// <summary>Host-protected service-account key JSON. ADC requires no stored key.</summary>
public sealed record LlmGoogleCredentials
{
    public required string ServiceAccountJson { get; init; }
    public override string ToString() => "LlmGoogleCredentials { protected }";
}
