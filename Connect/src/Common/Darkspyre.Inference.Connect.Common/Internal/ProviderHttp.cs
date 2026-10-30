using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>The fields of a provider error object, read from the unredacted body.</summary>
internal readonly record struct ProviderError(string? Code, string? Type, string? Message, string? Param);

/// <summary>How a provider reads its own HTTP error objects.</summary>
internal sealed record ProviderErrorReading(Func<ProviderError, bool> IsContextOverflow)
{
    /// <summary>The settings a 400 response refused, or null when the provider names none.</summary>
    public Func<ProviderError, LlmRefusedSettings>? RefusedSettings { get; init; }

    /// <summary>No overflow and no refused settings.</summary>
    public static ProviderErrorReading None { get; } = new(_ => false);

    /// <summary>The Responses protocol's <c>context_length_exceeded</c> code.</summary>
    public static ProviderErrorReading Responses { get; } = new(error => LlmContextOverflow.Responses(error.Code));
}

internal static class ProviderHttp
{
    /// <summary>
    /// The client timeout of the generation clients. A non-streaming response sends its headers only when generation
    /// ends, so these clients have no client timeout; the caller's cancellation token ends a request.
    /// </summary>
    internal static readonly TimeSpan GenerationTimeout = Timeout.InfiniteTimeSpan;

    private static readonly string[] ReservedHeaders = ["Authorization", "Accept", "Content-Type", "Host"];

    internal static void ValidateAdditionalHeaders(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        using var request = new HttpRequestMessage();
        foreach ((string name, string value) in headers)
            AddCustomHeader(request, name, value, "additional");
    }

    public static void ApplyHeaders(
        HttpRequestMessage request,
        LlmConnection connection,
        bool applyApiKeyAsBearer = true)
    {
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if (applyApiKeyAsBearer && !string.IsNullOrWhiteSpace(credential?.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.ApiKey);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach ((string name, string value) in connection.AdditionalHeaders)
        {
            AddCustomHeader(request, name, value, "additional");
        }

        if (credential is null)
        {
            return;
        }

        foreach ((string name, string value) in credential.SecretHeaders)
        {
            if (connection.AdditionalHeaders.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"Header '{name}' cannot be configured as both an additional and a secret header.");
            }

            AddCustomHeader(request, name, value, "secret");
        }
    }

    public static Uri BuildUri(LlmConnection connection, string relativePath)
    {
        if (!Uri.TryCreate(connection.Endpoint.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("The LLM connection endpoint must be an absolute HTTP or HTTPS URL.");
        }

        return new Uri(baseUri, relativePath.TrimStart('/'));
    }

    public static async Task ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        LlmTransport transport,
        LlmCredential? credential,
        string operation,
        IEnumerable<string> additionalSecrets,
        ProviderErrorReading reading,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
        (string? actualCode, string? actualMessage, string? actualType, string? actualParam) = ReadProviderError(body);
        ProviderError error = new(actualCode, actualType, actualMessage, actualParam);
        bool overflow = reading.IsContextOverflow(error);
        string[] secrets = additionalSecrets.ToArray();
        string sanitized = SecretRedactor.Sanitize(body, credential, secrets);
        (string? code, string? providerMessage, _, _) = ReadProviderError(sanitized);
        (long? reportedTokens, long? reportedLimit) = overflow ? ReadReportedSize(body) : (null, null);
        string? requestId = ReadRequestId(response, credential, secrets);
        string summary = string.IsNullOrWhiteSpace(providerMessage) ? sanitized : providerMessage;
        string message = $"{operation} failed ({(int)response.StatusCode}, request {requestId ?? "unknown"}): {summary}";

        throw new LlmProviderException(
            message,
            transport,
            response.StatusCode,
            code,
            requestId,
            response.Headers.RetryAfter?.Delta,
            sanitized,
            isContextOverflow: overflow)
        {
            ReportedInputTokens = reportedTokens,
            ReportedContextLimit = reportedLimit,
            RefusedSettings = response.StatusCode == HttpStatusCode.BadRequest && reading.RefusedSettings is { } refused
                ? refused(error) : LlmRefusedSettings.None
        };
    }

    /// <summary>The numeric n_prompt_tokens and n_ctx of a too-long refusal's error object (the llama.cpp server).</summary>
    private static (long? Tokens, long? Limit) ReadReportedSize(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement error = LlmJson.GetObject(document.RootElement, "error") ?? document.RootElement;
            return (Positive(error, "n_prompt_tokens"), Positive(error, "n_ctx"));
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static long? Positive(JsonElement error, string field) =>
            error.ValueKind == JsonValueKind.Object && error.TryGetProperty(field, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) && number > 0 ? number : null;
    }

    public static string? ReadRequestId(HttpResponseMessage response, LlmCredential? credential = null,
        IEnumerable<string>? extraSecrets = null)
    {
        foreach (string headerName in new[] { "x-request-id", "request-id", "x-trace-id", "apim-request-id" })
        {
            if (response.Headers.TryGetValues(headerName, out IEnumerable<string>? values))
            {
                string? id = values.FirstOrDefault();
                return id is not null && (credential is not null || extraSecrets is not null)
                    ? SecretRedactor.Sanitize(id, credential, extraSecrets ?? []) : id;
            }
        }

        return null;
    }

    private static void AddCustomHeader(
        HttpRequestMessage request,
        string name,
        string value,
        string kind)
    {
        if (ReservedHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The {kind} header '{name}' is reserved.");
        }

        if (name.Contains('\r')
            || name.Contains('\n')
            || value.Contains('\r')
            || value.Contains('\n'))
        {
            throw new InvalidOperationException($"The {kind} header '{name}' contains a newline.");
        }

        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            throw new InvalidOperationException($"The {kind} header '{name}' is invalid.");
        }
    }

    internal static (string? Code, string? Message, string? Type, string? Param) ReadProviderError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            JsonElement error = LlmJson.GetObject(root, "error") ?? root;
            return (LlmJson.GetString(error, "code"), LlmJson.GetString(error, "message"), LlmJson.GetString(error, "type"),
                LlmJson.GetString(error, "param"));
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
    }
}
