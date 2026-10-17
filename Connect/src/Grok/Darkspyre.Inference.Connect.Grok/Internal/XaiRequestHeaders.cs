using System.Net.Http.Headers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// The Grok subscription route. A Grok sign-in is served by xAI's subscription proxy, the host the Grok Build CLI uses;
/// api.x.ai serves API keys only.
/// </summary>
internal static class XaiRequestHeaders
{
    /// <summary>The subscription proxy's base URL (Grok Build xai-grok-env PRODUCTION_ENDPOINTS).</summary>
    internal const string SubscriptionEndpoint = "https://cli-chat-proxy.grok.com/v1";

    /// <summary>The Grok Build version sent in x-grok-client-version; the proxy refuses clients it does not know.</summary>
    internal const string ClientVersion = "1.0.45";

    internal static void Apply(HttpRequestMessage message, LlmConnection connection, bool stream)
    {
        AuthenticationHeaderValue authorization = Require(connection);
        ProviderHttp.ApplyHeaders(message, connection, applyApiKeyAsBearer: false);
        message.Headers.Authorization = authorization;
        // The proxy's auth middleware reads the Grok sign-in only with these headers (Grok Build proxy_headers.rs).
        message.Headers.TryAddWithoutValidation("X-XAI-Token-Auth", "xai-grok-cli");
        message.Headers.TryAddWithoutValidation("x-authenticateresponse", "authenticate-response");
        message.Headers.TryAddWithoutValidation("x-grok-client-version", ClientVersion);
        message.Headers.TryAddWithoutValidation("x-grok-client-identifier", "darkspyre-agent");
        message.Headers.TryAddWithoutValidation("x-grok-client-mode", "headless");
        message.Headers.Accept.Clear();
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
    }

    internal static AuthenticationHeaderValue Require(LlmConnection connection)
    {
        ValidateEndpoint(connection);
        return XaiAuthorization.Require(connection);
    }

    internal static void ValidateEndpoint(LlmConnection connection)
    {
        RuntimeInputAdmission.Validate(connection);
        if (connection.Transport != LlmTransport.XaiResponses) throw new ArgumentException("Select the Grok Responses transport.", nameof(connection));
        XaiConnectionOptions options = connection.Xai ?? new();
        if (!Enum.IsDefined(options.ServiceTier)) throw new ArgumentOutOfRangeException(nameof(connection), "Select an xAI service tier.");
        if (!string.Equals(connection.Endpoint.TrimEnd('/'), SubscriptionEndpoint, StringComparison.Ordinal))
            throw new ArgumentException("This Grok connection uses an old endpoint. Open its settings and save it to move it to the Grok subscription service.", nameof(connection));
    }
}
