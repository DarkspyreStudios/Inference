using System.Net;
using System.Net.WebSockets;
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
        IEnumerable<string> additionalSecrets, CancellationToken cancellationToken) =>
        ThrowIfUnsuccessfulCoreAsync(response, credential, additionalSecrets,
            false, "Provider rejected the speech request.", cancellationToken);

    internal static Task ThrowIfUnsuccessfulImageAsync(HttpResponseMessage response, LlmCredential? credential,
        IEnumerable<string> additionalSecrets, CancellationToken cancellationToken) =>
        ThrowIfUnsuccessfulCoreAsync(response, credential, additionalSecrets,
            false, "Provider rejected the image request.", cancellationToken);

    internal static Task ThrowIfUnsuccessfulVideoAsync(HttpResponseMessage response, LlmCredential? credential,
        IEnumerable<string> additionalSecrets, CancellationToken cancellationToken) =>
        ThrowIfUnsuccessfulCoreAsync(response, credential, additionalSecrets,
            true, "Provider rejected the video request.", cancellationToken);

    internal static Task ThrowIfUnsuccessfulEmbeddingAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ThrowIfUnsuccessfulCoreAsync(response, null, [], false, "Provider rejected the embedding request.", cancellationToken);
    }

    internal static Task ThrowIfUnsuccessfulDocumentSearchAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ThrowIfUnsuccessfulCoreAsync(response, null, [], false, "Provider rejected the document-search request.", cancellationToken);
    }

    private static async Task ThrowIfUnsuccessfulCoreAsync(HttpResponseMessage response, LlmCredential? credential,
        IEnumerable<string> additionalSecrets, bool diagnosticBody, string diagnosticMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode) return;

        string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string sanitizedBody = SecretRedactor.Sanitize(body, credential, additionalSecrets);
        string? requestId = ProviderHttp.ReadRequestId(response, credential, additionalSecrets);
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        (string? code, string? message, _) = ReadError(body);
        (string? safeCode, string? safeMessage, bool fromJson) = ReadError(sanitizedBody);
        string safe = diagnosticBody ? sanitizedBody
            : fromJson && !string.IsNullOrWhiteSpace(safeMessage) ? safeMessage : diagnosticMessage;
        throw Classify(response.StatusCode, code, message, safe, requestId: requestId, retryAfter: retryAfter,
            diagnosticCode: safeCode);
    }

    internal static Exception SpeechUpgradeFailure(ClientWebSocket socket, Exception original,
        LlmCredential? credential, HttpRequestMessage request, HttpClient http)
    {
        var evidence = new SpeechTransportEvidence(original);
        HttpStatusCode status = socket.HttpStatusCode;
        if (status == 0 || status == HttpStatusCode.SwitchingProtocols)
            return new InferenceException(InferenceFailureCode.ExecutionFailed, "The speech connection could not be opened.", evidence);

        // The BCL exposes upgrade headers and status, not the failed response body.
        using var response = new HttpResponseMessage(status);
        if (socket.HttpResponseHeaders is { } headers)
            foreach (var header in headers) response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        string[] secrets = SpeechHttpSecrets(request, http);
        return Classify(status, null, null, "Provider rejected the speech request.", evidence,
            ProviderHttp.ReadRequestId(response, credential, secrets), response.Headers.RetryAfter?.Delta);
    }

    internal static HttpRequestException SpeechHttpFailure(HttpRequestException original,
        LlmCredential? credential, HttpRequestMessage request, HttpClient http) => new(
        original.HttpRequestError,
        SecretRedactor.Sanitize(original.Message, credential, SpeechHttpSecrets(request, http)),
        new SpeechTransportEvidence(original), original.StatusCode);

    private static string[] SpeechHttpSecrets(HttpRequestMessage request, HttpClient http) =>
        request.Headers.SelectMany(header => header.Value)
            .Concat(http.DefaultRequestHeaders.SelectMany(header => header.Value))
            .Concat(new[] { request.Headers.Authorization?.Parameter, http.DefaultRequestHeaders.Authorization?.Parameter }.OfType<string>())
            .ToArray();

    private static Exception Classify(HttpStatusCode status, string? code, string? message, string safe,
        Exception? evidence = null, string? requestId = null, TimeSpan? retryAfter = null, string? diagnosticCode = null)
    {
        bool requiresSignIn = status == HttpStatusCode.Unauthorized
            || (status == HttpStatusCode.Forbidden
                && string.Equals(message?.Trim(), InvalidTokenMessage, StringComparison.Ordinal))
            || (status == HttpStatusCode.BadRequest && code is "invalid_grant" or "invalid_token");
        if (requiresSignIn)
        {
            return new InferenceException(InferenceFailureCode.AuthenticationRequired,
                "Grok authorization is no longer valid; sign in again.",
                Failure(status, "xai_authentication_required", safe, evidence, requestId, retryAfter));
        }

        if (status == HttpStatusCode.PaymentRequired)
        {
            return EntitlementFailure(status, safe, evidence, requestId, retryAfter);
        }

        // Subscription-specific 403 matching requires a qualified provider response.
        return Failure(status, diagnosticCode, safe, evidence, requestId, retryAfter, LlmContextOverflow.Responses(code));
    }

    internal static InferenceException EntitlementFailure(HttpStatusCode status, string safe, Exception? evidence = null,
        string? requestId = null, TimeSpan? retryAfter = null) =>
        new(InferenceFailureCode.AccessDenied,
            $"Grok requires an eligible subscription or available credits: {safe}",
            Failure(status, "xai_entitlement_required", safe, evidence, requestId, retryAfter));

    private static LlmProviderException Failure(HttpStatusCode status, string? code, string safe, Exception? evidence = null,
        string? requestId = null, TimeSpan? retryAfter = null, bool isContextOverflow = false) =>
        new($"Grok request failed ({(int)status}): {safe}", LlmTransport.XaiResponses,
            status, code, requestId, retryAfter, sanitizedResponseBody: safe, innerException: evidence,
            isContextOverflow: isContextOverflow);

    private sealed class SpeechTransportEvidence(Exception original) : Exception("The speech transport reported a failure.")
    {
        internal Exception Original { get; } = original;
    }

    private static (string? Code, string? Message, bool FromJson) ReadError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            JsonElement error = LlmJson.GetObject(root, "error") ?? root;
            return (LlmJson.GetString(error, "code") ?? LlmJson.GetString(root, "error"),
                LlmJson.GetString(error, "message") ?? LlmJson.GetString(root, "error_description")
                ?? LlmJson.GetString(root, "error"), true);
        }
        catch (JsonException)
        {
            return (null, body, false);
        }
    }
}
