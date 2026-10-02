using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Azure;

internal static class AzureEndpoints
{
    internal static Uri Validate(LlmConnection connection, string? deployment = null, bool requireCredential = true)
    {
        RuntimeInputAdmission.Validate(connection);
        if (connection.Transport is not (LlmTransport.AzureOpenAiResponses or LlmTransport.AzureOpenAiChat)
            || connection.Azure is not { ApiMode: LlmAzureApiMode.V1, AuthenticationMode: LlmAzureAuthenticationMode.ApiKey } config)
            throw new ArgumentException("Select an Azure v1 transport and API-key configuration.");
        if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || !(endpoint.OriginalString.Equals(endpoint.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                || endpoint.OriginalString.Equals(endpoint.GetLeftPart(UriPartial.Authority) + "/", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Use only an HTTPS Azure resource authority without paths, credentials, query or fragment.");
        if (config.ApiVersion is not null || config.Entra is not null)
            throw new NotSupportedException("The selected Azure route does not implement versioned routing or Entra authentication.");
        if (!AzureConfigurationData.ValidId(config.DeploymentId) || config.ModelProfileId is { } profile && !AzureConfigurationData.ValidId(profile))
            throw new ArgumentException("Supply an exact deployment name and a nonempty underlying model profile when configured.");
        if (deployment is not null && deployment != config.DeploymentId)
            throw new ArgumentException("The request model must match the configured Azure deployment.");
        if (connection.Vertex is not null)
            throw new NotSupportedException("Azure cannot use Google routing configuration.");
        if (connection.Transport == LlmTransport.AzureOpenAiResponses && connection.ChatCompletions is not null)
            throw new NotSupportedException("Responses cannot use Chat Completions configuration.");
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if (connection.AdditionalHeaders.Keys.Any(IsAuthenticationHeader)
            || credential?.SecretHeaders.Keys.Any(IsAuthenticationHeader) == true)
            throw new ArgumentException("Azure owns Authorization and api-key headers.");
        if (credential is not null && (credential.OAuth is not null || credential.Aws is not null || credential.Google is not null
            || credential.Azure is not null || credential.Copilot is not null || credential.CopilotRuntime is not null))
            throw new ArgumentException("Azure API-key mode accepts no other authentication credential.");
        if (requireCredential && string.IsNullOrWhiteSpace(credential?.ApiKey)
            || credential?.ApiKey?.Any(char.IsControl) == true)
            throw new ArgumentException("A valid protected Azure resource API key is required.");
        return endpoint;
    }

    internal static Uri Generation(LlmConnection connection, string deployment)
    {
        Uri endpoint = Validate(connection, deployment);
        return new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/openai/v1/"
            + (connection.Transport == LlmTransport.AzureOpenAiResponses ? "responses" : "chat/completions"));
    }

    internal static void Authorize(HttpRequestMessage request, LlmConnection connection)
    {
        _ = Validate(connection);
        ProviderHttp.ApplyHeaders(request, connection, applyApiKeyAsBearer: false);
        request.Headers.Add("api-key", LlmCredential.Parse(connection.Credential)!.ApiKey);
    }

    private static bool IsAuthenticationHeader(string name) => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("api-key", StringComparison.OrdinalIgnoreCase);

}
