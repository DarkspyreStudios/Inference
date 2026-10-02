using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class XaiAuthorization
{
    internal static AuthenticationHeaderValue Require(LlmConnection connection)
    {
        if (!TryGet(LlmCredential.Parse(connection.Credential), out AuthenticationHeaderValue? authorization))
            throw new InferenceException(InferenceFailureCode.AuthenticationRequired, "Grok requires a valid OAuth credential.");
        return authorization;
    }

    internal static bool IsValid(LlmCredential? credential) => TryGet(credential, out _);

    private static bool TryGet(LlmCredential? credential, [NotNullWhen(true)] out AuthenticationHeaderValue? authorization)
    {
        authorization = null;
        if (credential is null || !string.IsNullOrWhiteSpace(credential.ApiKey) || credential.Aws is not null
            || credential.Google is not null || credential.Azure is not null || credential.Copilot is not null
            || credential.CopilotRuntime is not null || credential.SecretHeaders.Count != 0
            || credential.OAuth?.AccessToken is not { Length: > 0 } token || token.Any(char.IsWhiteSpace))
            return false;
        return AuthenticationHeaderValue.TryParse("Bearer " + token, out authorization)
            && authorization.Scheme == "Bearer" && authorization.Parameter == token;
    }
}
