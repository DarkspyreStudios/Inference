using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Reads a rejected Google response once without exposing its body in public diagnostics.</summary>
internal static class GoogleProviderFailure
{
    internal static async Task ThrowIfUnsuccessfulAsync(HttpResponseMessage response, LlmTransport transport,
        string message, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        string body;
        try
        {
            body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException canceled)
        {
            throw new OperationCanceledException(message, new ReadFailureEvidence(canceled), canceled.CancellationToken);
        }
        catch (Exception error)
        {
            throw new LlmProviderException(message, transport, response.StatusCode,
                retryAfter: retryAfter, innerException: new ReadFailureEvidence(error));
        }
        bool overflow = false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                overflow = LlmContextOverflow.Google(document.RootElement);
        }
        catch (JsonException)
        {
            // A malformed error body supplies no context-overflow evidence.
        }

        throw new LlmProviderException(message, transport, response.StatusCode,
            retryAfter: retryAfter, isContextOverflow: overflow);
    }

    private sealed class ReadFailureEvidence(Exception original) : Exception("The Google error response could not be read.")
    {
        internal Exception Original { get; } = original;
    }
}
