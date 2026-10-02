using System.Net.Http.Headers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class XaiRequestHeaders
{
    internal static void Apply(HttpRequestMessage message, LlmConnection connection, bool stream)
    {
        AuthenticationHeaderValue authorization = Require(connection);
        ProviderHttp.ApplyHeaders(message, connection, applyApiKeyAsBearer: false);
        message.Headers.Authorization = authorization;
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
        if (!string.Equals(connection.Endpoint.TrimEnd('/'), options.GetEndpoint(), StringComparison.Ordinal))
            throw new ArgumentException("The Grok endpoint must match its typed region.", nameof(connection));
    }
}
