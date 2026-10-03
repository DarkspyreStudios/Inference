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

    internal static Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        LlmCredential? credential,
        IEnumerable<string> additionalSecrets,
        CancellationToken cancellationToken,
        bool diagnosticBody = true) =>
        ThrowIfUnsuccessfulCoreAsync(response, credential, additionalSecrets,
            diagnosticBody, "Provider rejected the Files request.", cancellationToken);

    internal static Task ThrowIfUnsuccessfulSpeechAsync(HttpResponseMessage response, LlmCredential? credential,
        CancellationToken cancellationToken) =>
        ThrowIfUnsuccessfulCoreAsync(response, credential, [],
            false, "Provider rejected the speech request.", cancellationToken);

    private static async Task ThrowIfUnsuccessfulCoreAsync(HttpResponseMessage response, LlmCredential? credential,
        IEnumerable<string> additionalSecrets, bool diagnosticBody, string diagnosticMessage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode) return;

        string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string safe = diagnosticBody ? SecretRedactor.Sanitize(body, credential, additionalSecrets)
            : diagnosticMessage;
        (string? code, string? message) = ReadError(body);
        throw Classify(response.StatusCode, code, message, safe);
    }

    internal static Exception SpeechUpgradeFailure(HttpStatusCode status, Exception original)
    {
        var evidence = new SpeechTransportEvidence(original);
        // The BCL exposes status, not the failed upgrade's body. Do not infer body-dependent facts.
        return status != 0 && status != HttpStatusCode.SwitchingProtocols
            ? Classify(status, null, null, "Provider rejected the speech request.", evidence)
            : new InferenceException(InferenceFailureCode.ExecutionFailed, "The speech connection could not be opened.", evidence);
    }

    private static Exception Classify(HttpStatusCode status, string? code, string? message, string safe,
        Exception? evidence = null)
    {
        bool requiresSignIn = status == HttpStatusCode.Unauthorized
            || (status == HttpStatusCode.Forbidden
                && string.Equals(message?.Trim(), InvalidTokenMessage, StringComparison.Ordinal))
            || (status == HttpStatusCode.BadRequest && code is "invalid_grant" or "invalid_token");
        if (requiresSignIn)
        {
            return new InferenceException(InferenceFailureCode.AuthenticationRequired,
                "Grok authorization is no longer valid; sign in again.",
                Failure(status, "xai_authentication_required", safe, evidence));
        }

        if (status == HttpStatusCode.PaymentRequired)
        {
            return EntitlementFailure(status, safe, evidence);
        }

        // Subscription-specific 403 matching requires a qualified provider response.
        return Failure(status, null, safe, evidence);
    }

    internal static InferenceException EntitlementFailure(HttpStatusCode status, string safe, Exception? evidence = null) =>
        new(InferenceFailureCode.AccessDenied,
            $"Grok requires an eligible subscription or available credits: {safe}",
            Failure(status, "xai_entitlement_required", safe, evidence));

    private static LlmProviderException Failure(HttpStatusCode status, string? code, string safe, Exception? evidence = null) =>
        new($"Grok request failed ({(int)status}): {safe}", LlmTransport.XaiResponses,
            status, code, sanitizedResponseBody: safe, innerException: evidence);

    private sealed class SpeechTransportEvidence(Exception original) : Exception("The speech transport reported an opening failure.")
    {
        internal Exception Original { get; } = original;
    }

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
