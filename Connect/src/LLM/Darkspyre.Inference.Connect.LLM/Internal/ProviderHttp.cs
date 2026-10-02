using System.Net.Http.Headers;
using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

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
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string sanitized = SecretRedactor.Sanitize(body, credential);
        (string? code, string? providerMessage) = ReadProviderError(sanitized);
        string requestId = ReadRequestId(response) ?? "unknown";
        string summary = string.IsNullOrWhiteSpace(providerMessage) ? sanitized : providerMessage;
        string message = $"{operation} failed ({(int)response.StatusCode}, request {requestId}): {summary}";

        throw new LlmProviderException(
            message,
            transport,
            response.StatusCode,
            code,
            ReadRequestId(response),
            response.Headers.RetryAfter?.Delta,
            sanitized);
    }

    public static string? ReadRequestId(HttpResponseMessage response)
    {
        foreach (string headerName in new[] { "x-request-id", "request-id", "x-trace-id", "apim-request-id" })
        {
            if (response.Headers.TryGetValues(headerName, out IEnumerable<string>? values))
            {
                return values.FirstOrDefault();
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

    private static (string? Code, string? Message) ReadProviderError(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            JsonElement error = LlmJson.GetObject(root, "error") ?? root;
            return (LlmJson.GetString(error, "code"), LlmJson.GetString(error, "message"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
