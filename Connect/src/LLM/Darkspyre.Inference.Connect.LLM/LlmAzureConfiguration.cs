namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The Azure resource API selected by this connection.</summary>
public enum LlmAzureApiMode
{
    /// <summary>The resource's OpenAI v1 API.</summary>
    V1 = 0
}

/// <summary>The explicitly selected Azure authentication scheme.</summary>
public enum LlmAzureAuthenticationMode
{
    /// <summary>A protected resource API key, sent only in the api-key header.</summary>
    ApiKey = 0
}

/// <summary>Azure-owned routing and model evidence. The endpoint supplies only the HTTPS resource authority.</summary>
public sealed record LlmAzureConfiguration
{
    public LlmAzureApiMode ApiMode { get; init; } = LlmAzureApiMode.V1;
    public LlmAzureAuthenticationMode AuthenticationMode { get; init; } = LlmAzureAuthenticationMode.ApiKey;

    /// <summary>The deployment name used as the wire model. Requests must select this same deployment.</summary>
    public string DeploymentId { get; init; } = string.Empty;

    /// <summary>An optional underlying model profile. This is not a deployment name or access guarantee.</summary>
    public string? ModelProfileId { get; init; }
}
