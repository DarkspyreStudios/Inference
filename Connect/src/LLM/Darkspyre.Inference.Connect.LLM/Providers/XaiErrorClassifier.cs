using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Shared xAI HTTP failure policy for Connect adapters.</summary>
public static class XaiErrorClassifier
{
    private const string InvalidTokenMessage = "The OAuth2 access token could not be validated";

    /// <summary>Throws a sanitized failure without refreshing or retrying the request.</summary>
    public static Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        ProviderAuthorizationHeader? authorization = null,
        CancellationToken cancellationToken = default) =>
        ThrowIfUnsuccessfulAsync(response, null,
            authorization is { } header ? [header.Value] : [], cancellationToken);

    internal static async Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        LlmCredential? credential,
        IEnumerable<string> additionalSecrets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode) return;

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string safe = SecretRedactor.Sanitize(body, credential, additionalSecrets);
        (string? code, string? message) = ReadError(body);
        bool requiresSignIn = response.StatusCode == HttpStatusCode.Unauthorized
            || (response.StatusCode == HttpStatusCode.Forbidden
                && string.Equals(message?.Trim(), InvalidTokenMessage, StringComparison.Ordinal))
            || (response.StatusCode == HttpStatusCode.BadRequest && code is "invalid_grant" or "invalid_token");
        if (requiresSignIn)
        {
            throw new InferenceException(InferenceFailureCode.AuthenticationRequired,
                "Grok authorization is no longer valid; sign in again.",
                Failure(response.StatusCode, "xai_authentication_required", safe));
        }

        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            throw EntitlementFailure(response.StatusCode, safe);
        }

        // Subscription-specific 403 matching requires a qualified provider response.
        throw Failure(response.StatusCode, null, safe);
    }

    internal static InferenceException EntitlementFailure(HttpStatusCode status, string safe) =>
        new(InferenceFailureCode.AccessDenied,
            $"Grok requires an eligible subscription or available credits: {safe}",
            Failure(status, "xai_entitlement_required", safe));

    private static LlmProviderException Failure(HttpStatusCode status, string? code, string safe) =>
        new($"Grok request failed ({(int)status}): {safe}", LlmTransport.XaiResponses,
            status, code, sanitizedResponseBody: safe);

    private static (string? Code, string? Message) ReadError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            JsonElement error = LlmJson.GetObject(root, "error") ?? root;
            return (LlmJson.GetString(root, "error") ?? LlmJson.GetString(error, "code"),
                LlmJson.GetString(error, "message") ?? LlmJson.GetString(root, "error_description")
                ?? LlmJson.GetString(root, "error"));
        }
        catch (JsonException)
        {
            return (null, body);
        }
    }
}
