using System.Net;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>A provider or protocol failure with diagnostics safe to display and persist.</summary>
public sealed class LlmProviderException : Exception
{
    /// <summary>Creates a provider failure with safe display text and explicit failure classifications.</summary>
    public LlmProviderException(
        string message,
        LlmTransport transport,
        HttpStatusCode? statusCode = null,
        string? providerCode = null,
        string? requestId = null,
        TimeSpan? retryAfter = null,
        string? sanitizedResponseBody = null,
        Exception? innerException = null,
        bool isContextOverflow = false,
        bool isResponseStreamInterrupted = false)
        : base(message, innerException)
    {
        Transport = transport;
        StatusCode = statusCode;
        ProviderCode = providerCode;
        RequestId = requestId;
        RetryAfter = retryAfter;
        SanitizedResponseBody = sanitizedResponseBody;
        IsContextOverflow = isContextOverflow;
        IsResponseStreamInterrupted = isResponseStreamInterrupted;
    }

    /// <summary>The transport the failed request used.</summary>
    public LlmTransport Transport { get; }

    /// <summary>The HTTP status the provider returned, when the failure came from an HTTP response.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>The provider or client error code that classifies the failure, when one is known.</summary>
    public string? ProviderCode { get; }

    /// <summary>The request id the provider assigned to the failed request, when it reported one.</summary>
    public string? RequestId { get; }

    /// <summary>The delay the provider asked for before a retry, when it sent one.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The response body with secrets removed, safe to display and persist.</summary>
    public string? SanitizedResponseBody { get; }

    /// <summary>The provider rejected input that exceeds the model's context window.</summary>
    public bool IsContextOverflow { get; }

    /// <summary>The response body stream is interrupted before its terminal response arrives.</summary>
    /// <gotcha>This classification does not authorize replay of requests enabling provider-hosted tools.</gotcha>
    public bool IsResponseStreamInterrupted { get; }

    /// <summary>Usage the provider reported for the failed response, when it reported any.</summary>
    public Darkspyre.Inference.Common.LLM.LlmUsage? Usage { get; init; }
}
