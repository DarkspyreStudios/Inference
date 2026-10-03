using System.Net;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>A provider or protocol failure with diagnostics safe to display and persist.</summary>
public sealed class LlmProviderException : Exception
{
    public LlmProviderException(
        string message,
        LlmTransport transport,
        HttpStatusCode? statusCode = null,
        string? providerCode = null,
        string? requestId = null,
        TimeSpan? retryAfter = null,
        string? sanitizedResponseBody = null,
        Exception? innerException = null,
        bool isContextOverflow = false)
        : base(message, innerException)
    {
        Transport = transport;
        StatusCode = statusCode;
        ProviderCode = providerCode;
        RequestId = requestId;
        RetryAfter = retryAfter;
        SanitizedResponseBody = sanitizedResponseBody;
        IsContextOverflow = isContextOverflow;
    }

    public LlmTransport Transport { get; }

    public HttpStatusCode? StatusCode { get; }

    public string? ProviderCode { get; }

    public string? RequestId { get; }

    public TimeSpan? RetryAfter { get; }

    public string? SanitizedResponseBody { get; }

    /// <summary>The provider rejected input that exceeds the model's context window.</summary>
    public bool IsContextOverflow { get; }
}
