using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Sends an already-authorized create request. Routing and receipt authority remain the caller's responsibility.</summary>
internal static class GoogleCacheHttp
{
    internal static Task<LlmContextCache> GetAsync(HttpClient http, HttpRequestMessage request,
        LlmTransport transport, Func<JsonElement, LlmContextCache> parseReceipt,
        Action<CancellationToken> ownerFence, CancellationToken cancellationToken) =>
        ReadAsync(http, request, transport, parseReceipt, ownerFence, cancellationToken);

    internal static Task<LlmContextCachePage> ListAsync(HttpClient http, HttpRequestMessage request,
        LlmTransport transport, Func<JsonElement, LlmContextCachePage> parsePage,
        Action<CancellationToken> ownerFence, CancellationToken cancellationToken) =>
        ReadAsync(http, request, transport, parsePage, ownerFence, cancellationToken);

    private static async Task<T> ReadAsync<T>(HttpClient http, HttpRequestMessage request,
        LlmTransport transport, Func<JsonElement, T> parse, Action<CancellationToken> ownerFence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(ownerFence);
        if (request.Method != HttpMethod.Get) throw new ArgumentException("Cache reads require a GET request.", nameof(request));
        using var consumption = SdkHttpConsumption.Enter(http, cancellationToken);
        cancellationToken = consumption.Token;
        cancellationToken.ThrowIfCancellationRequested();
        ownerFence(cancellationToken);
        consumption.Fence();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LlmProviderException("Google rejected cache reading.", transport, response.StatusCode,
                requestId: ProviderHttp.ReadRequestId(response), retryAfter: response.Headers.RetryAfter?.Delta);
        string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        return parse(document.RootElement);
    }

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
        LlmProviderException? ownedStatusFailure = null;
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
            {
                ownedStatusFailure = new LlmProviderException("Google rejected cache creation.", transport, status,
                    requestId: requestId, retryAfter: retryAfter);
                throw ownedStatusFailure;
            }
            category = "Google cache create returned invalid or unavailable receipt facts.";
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
            Fence();
            using JsonDocument document = JsonDocument.Parse(body);
            observed = parseReceipt(document.RootElement) ?? throw new JsonException();
            Fence();
        }
        catch (Exception error)
        {
            canceled = error is OperationCanceledException;
            failure = ReferenceEquals(error, ownedStatusFailure) ? error
                : SafeFailure(error, category, transport, status, requestId, retryAfter, cancellationToken);
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
            failure ??= retirement;
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
        HttpStatusCode? status, string? requestId, TimeSpan? retryAfter, CancellationToken token)
    {
        if (error is OperationCanceledException)
            return new OperationCanceledException("Google cache creation was interrupted.", token);
        // Even a caller/parser LlmProviderException can contain raw credentials or cached content.
        // Only primitive status facts survive; response identifiers remain data, never message text.
        status ??= error switch
        {
            HttpRequestException request => request.StatusCode,
            LlmProviderException provider => provider.StatusCode,
            _ => null
        };
        return new LlmProviderException(category, transport, status, requestId: requestId, retryAfter: retryAfter);
    }
}
