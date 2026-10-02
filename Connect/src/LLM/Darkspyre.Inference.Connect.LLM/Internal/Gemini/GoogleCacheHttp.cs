using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Sends an already-authorized create request. Routing and receipt authority remain the caller's responsibility.</summary>
internal static class GoogleCacheHttp
{
    [SuppressMessage("Design", "CA1031", Justification = "The provider boundary reports uncertain mutation and attempts owned response cleanup for every failure.")]
    internal static async Task<LlmContextCache> CreateAsync(HttpClient http, HttpRequestMessage request,
        LlmTransport transport, LlmContextCacheCreateCorrelation correlation,
        Func<JsonElement, LlmContextCache> parseReceipt, Action<CancellationToken> ownerFence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(parseReceipt);
        ArgumentNullException.ThrowIfNull(ownerFence);
        using var consumption = SdkHttpConsumption.Enter(http, cancellationToken);
        cancellationToken = consumption.Token;
        void Fence()
        {
            cancellationToken.ThrowIfCancellationRequested();
            ownerFence(cancellationToken);
            consumption.Fence();
        }

        // Failures before invoking SendAsync do not imply a dispatched mutation.
        Fence();
        HttpResponseMessage? response = null;
        LlmContextCache? observed = null;
        string? requestId = null;
        HttpStatusCode? status = null;
        TimeSpan? retryAfter = null;
        Exception? failure = null;
        bool canceled = false;
        string category = "Google cache create transport failed.";
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            requestId = ProviderHttp.ReadRequestId(response);
            retryAfter = response.Headers.RetryAfter?.Delta;
            Fence();
            if (!response.IsSuccessStatusCode)
                throw new LlmProviderException("Google rejected cache creation.", transport, status,
                    requestId: requestId, retryAfter: retryAfter);
            category = "Google cache create returned invalid or unavailable receipt facts.";
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
            Fence();
            using JsonDocument document = JsonDocument.Parse(body);
            observed = parseReceipt(document.RootElement);
            Fence();
        }
        catch (Exception error)
        {
            canceled = error is OperationCanceledException;
            failure = SafeFailure(error, category, transport, status, requestId, retryAfter, cancellationToken);
        }
        if (response is not null)
        {
            try { response.Dispose(); }
            catch (Exception error)
            {
                Exception cleanup = SafeFailure(error, "Google cache create response cleanup failed.",
                    transport, status, requestId, retryAfter, cancellationToken);
                failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
                canceled |= error is OperationCanceledException;
            }
        }
        try { Fence(); }
        catch (Exception error)
        {
            canceled |= error is OperationCanceledException;
            Exception retirement = SafeFailure(error, "Google cache create was interrupted before publication.",
                transport, status, requestId, retryAfter, cancellationToken);
            failure = failure is null ? retirement : new AggregateException(failure, retirement);
        }
        if (failure is not null)
        {
            if (canceled)
                throw new LlmContextCacheCreateCancelledException(correlation, cancellationToken, failure, requestId, observed);
            throw new LlmContextCacheCreateUncertainException(correlation, failure, requestId, observed);
        }
        return observed!;
    }

    private static Exception SafeFailure(Exception error, string category, LlmTransport transport,
        HttpStatusCode? status, string? requestId, TimeSpan? retryAfter, CancellationToken token) => error;
}
