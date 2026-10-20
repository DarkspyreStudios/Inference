using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>The xAI HTTP failure policy of the Grok client, authenticator, catalog and embeddings.</summary>
internal static class XaiErrorClassifier
{
    private const string InvalidTokenMessage = "The OAuth2 access token could not be validated";

    internal static Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        LlmCredential? credential,
        IEnumerable<string> additionalSecrets,
        CancellationToken cancellationToken,
        bool diagnosticBody = true) =>
        ThrowIfUnsuccessfulCoreAsync(response, credential, additionalSecrets,
            diagnosticBody, "Provider rejected the request.", cancellationToken);

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
        bool? shouldRetry = response.Headers.TryGetValues("x-should-retry", out IEnumerable<string>? values)
            && bool.TryParse(values.FirstOrDefault(), out bool retry) ? retry : null;
        bool outOfCredits = response.StatusCode == HttpStatusCode.PaymentRequired
            || response.StatusCode == HttpStatusCode.TooManyRequests
                && (body.Contains("subscription:free-usage-exhausted", StringComparison.Ordinal) || body.Contains("spending-limit", StringComparison.Ordinal))
            || response.StatusCode == HttpStatusCode.Forbidden && body.Contains("run out of credits", StringComparison.OrdinalIgnoreCase);
        string entitlementMessage = Regex.Replace(safeMessage ?? sanitizedBody, @"\[WKE=[^\]]*\]", string.Empty,
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim().TrimEnd('.').TrimEnd();
        throw Classify(response.StatusCode, code, message, safe, requestId: requestId, retryAfter: retryAfter,
            diagnosticCode: safeCode, shouldRetry: shouldRetry, outOfCredits: outOfCredits, entitlementMessage: entitlementMessage);
    }

    internal static Exception HttpSendFailure(Exception original, LlmCredential? credential, string[] secrets)
    {
        string Safe(string value) => SecretRedactor.Sanitize(value, credential, secrets);
        var evidence = new SendEvidence(original);
        return original switch
        {
            AggregateException aggregate => new AggregateException("Grok HTTP request failed.",
                aggregate.InnerExceptions.Select(error => HttpSendFailure(error, credential, secrets))),
            OperationCanceledException canceled => new OperationCanceledException("Grok HTTP request was interrupted.", evidence, canceled.CancellationToken),
            InferenceException inference => new InferenceException(inference.Code, Safe(inference.Message),
                inference.InnerException is { } cause ? HttpSendFailure(cause, credential, secrets) : evidence),
            LlmProviderException provider => new LlmProviderException(Safe(provider.Message), LlmTransport.XaiResponses,
                provider.StatusCode, provider.ProviderCode is { } code ? Safe(code) : null,
                provider.RequestId is { } id ? Safe(id) : null, provider.RetryAfter,
                provider.SanitizedResponseBody is { } body ? Safe(body) : null, evidence, provider.IsContextOverflow),
            _ => new LlmProviderException(Safe(original.Message), LlmTransport.XaiResponses,
                (original as HttpRequestException)?.StatusCode, innerException: evidence)
        };
    }

    private sealed class SendEvidence(Exception original) : Exception("Grok HTTP send failed.")
    {
        internal Exception Original { get; } = original;
    }

    private static Exception Classify(HttpStatusCode status, string? code, string? message, string safe,
        Exception? evidence = null, string? requestId = null, TimeSpan? retryAfter = null, string? diagnosticCode = null,
        bool? shouldRetry = null, bool outOfCredits = false, string? entitlementMessage = null)
    {
        bool requiresSignIn = status == HttpStatusCode.Unauthorized
            || (status == HttpStatusCode.Forbidden
                && string.Equals(message?.Trim(), InvalidTokenMessage, StringComparison.Ordinal))
            || (status == HttpStatusCode.BadRequest && (code is "invalid_grant" or "invalid_token"
                || code is null && message is "invalid_grant" or "invalid_token"));
        if (requiresSignIn)
        {
            return new InferenceException(InferenceFailureCode.AuthenticationRequired,
                "Grok authorization is no longer valid; sign in again.",
                Failure(status, "xai_authentication_required", safe, evidence, requestId, retryAfter, shouldRetry: shouldRetry));
        }

        if (outOfCredits)
        {
            return EntitlementFailure(status, entitlementMessage!, evidence, requestId, retryAfter, shouldRetry);
        }

        return Failure(status, diagnosticCode, safe, evidence, requestId, retryAfter, LlmContextOverflow.Responses(code), shouldRetry);
    }

    private static InferenceException EntitlementFailure(HttpStatusCode status, string safe, Exception? evidence = null,
        string? requestId = null, TimeSpan? retryAfter = null, bool? shouldRetry = null) =>
        new(InferenceFailureCode.AccessDenied,
            $"Grok requires an eligible subscription or available credits: {safe}",
            Failure(status, "xai_entitlement_required", safe, evidence, requestId, retryAfter, shouldRetry: shouldRetry));

    private static LlmProviderException Failure(HttpStatusCode status, string? code, string safe, Exception? evidence = null,
        string? requestId = null, TimeSpan? retryAfter = null, bool isContextOverflow = false, bool? shouldRetry = null) =>
        new($"Grok request failed ({(int)status}): {safe}", LlmTransport.XaiResponses,
            status, code, requestId, retryAfter, sanitizedResponseBody: safe, innerException: evidence,
            isContextOverflow: isContextOverflow)
        { ShouldRetry = shouldRetry };

    private static (string? Code, string? Message, bool FromJson) ReadError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            JsonElement error = LlmJson.GetObject(root, "error") ?? root;
            return (LlmJson.GetString(error, "code"),
                LlmJson.GetString(error, "message") ?? LlmJson.GetString(root, "error_description")
                ?? LlmJson.GetString(root, "error"), true);
        }
        catch (JsonException)
        {
            return (null, body, false);
        }
    }
}
