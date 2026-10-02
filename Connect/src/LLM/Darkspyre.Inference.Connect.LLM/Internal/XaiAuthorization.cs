using System.Net.Http.Headers;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class XaiAuthorization
{
    internal static AuthenticationHeaderValue Require(LlmConnection connection)
    {
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if (credential is null || !string.IsNullOrWhiteSpace(credential.ApiKey) || credential.Aws is not null
            || credential.Google is not null || credential.Azure is not null || credential.Copilot is not null
            || credential.CopilotRuntime is not null || credential.SecretHeaders.Count != 0
            || credential.OAuth?.AccessToken is not { Length: > 0 } token || token.Any(char.IsWhiteSpace)
            || !AuthenticationHeaderValue.TryParse("Bearer " + token, out AuthenticationHeaderValue? authorization)
            || authorization.Scheme != "Bearer" || authorization.Parameter != token)
            throw new InferenceException(InferenceFailureCode.AuthenticationRequired, "Grok requires a valid OAuth credential.");
        return authorization;
    }
}
