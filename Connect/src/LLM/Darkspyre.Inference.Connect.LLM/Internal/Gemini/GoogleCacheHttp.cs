using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Sends already-authorized cache requests. Routing and receipt authority remain the caller's responsibility.</summary>
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

    internal static Task<LlmContextCache> UpdateExpiryAsync(HttpClient http, HttpRequestMessage request,
        LlmTransport transport, LlmExplicitCacheReference reference, Func<JsonElement, LlmContextCache> parseReceipt,
        Action<CancellationToken> ownerFence, CancellationToken cancellationToken) =>
        MutateAsync(http, request, transport, reference, LlmContextCacheMutation.UpdateExpiry,
            parseReceipt, null, null, ownerFence, cancellationToken);

    internal static Task<LlmContextCacheDeleteResult> DeleteAsync(HttpClient http, HttpRequestMessage request,
        LlmTransport transport, LlmExplicitCacheReference reference,
        Func<LlmExplicitCacheReference, bool> authorizeAlreadyAbsent,
        Action<CancellationToken> ownerFence, CancellationToken cancellationToken) =>
        MutateAsync<LlmContextCacheDeleteResult>(http, request, transport, reference, LlmContextCacheMutation.Delete,
            null, static absent => new(absent), authorizeAlreadyAbsent, ownerFence, cancellationToken);

    [SuppressMessage("Design", "CA1031", Justification = "The provider boundary reports uncertain mutation and attempts owned response cleanup for every failure.")]
    private static async Task<T> MutateAsync<T>(HttpClient http, HttpRequestMessage request,
        LlmTransport transport, LlmExplicitCacheReference reference, LlmContextCacheMutation operation,
        Func<JsonElement, T>? parseUpdate, Func<bool, T>? deletionReceipt,
        Func<LlmExplicitCacheReference, bool>? authorizeAlreadyAbsent,
        Action<CancellationToken> ownerFence, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(ownerFence);
        if (operation == LlmContextCacheMutation.UpdateExpiry) ArgumentNullException.ThrowIfNull(parseUpdate);
        else
        {
            ArgumentNullException.ThrowIfNull(deletionReceipt);
            ArgumentNullException.ThrowIfNull(authorizeAlreadyAbsent);
        }
        HttpMethod expected = operation == LlmContextCacheMutation.UpdateExpiry ? HttpMethod.Patch : HttpMethod.Delete;
        if (request.Method != expected) throw new ArgumentException("Cache mutation request method does not match its operation.", nameof(request));
        using var consumption = SdkHttpConsumption.Enter(http, cancellationToken);
        cancellationToken = consumption.Token;
        void Fence()
        {
            cancellationToken.ThrowIfCancellationRequested();
            ownerFence(cancellationToken);
            consumption.Fence();
        }
        Fence();
        HttpResponseMessage? response = null;
        T? observed = null;
        string? requestId = null;
        HttpStatusCode? status = null;
        TimeSpan? retryAfter = null;
        Exception? failure = null;
        LlmProviderException? ownedStatusFailure = null;
        bool canceled = false;
        bool dispatched = false;
        string category = "Google cache mutation transport failed.";
        try
        {
            Fence();
            dispatched = true;
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            requestId = ProviderHttp.ReadRequestId(response);
            retryAfter = response.Headers.RetryAfter?.Delta;
            Fence();
            bool alreadyAbsent = false;
            if (operation == LlmContextCacheMutation.Delete && status == HttpStatusCode.NotFound)
            {
                category = "Google cache deletion could not validate absence authority.";
                Fence();
                alreadyAbsent = authorizeAlreadyAbsent!(reference);
                Fence();
            }
            if (!response.IsSuccessStatusCode && !alreadyAbsent)
            {
                ownedStatusFailure = new LlmProviderException("Google rejected cache mutation.", transport, status,
                    requestId: requestId, retryAfter: retryAfter);
                throw ownedStatusFailure;
            }
            category = "Google cache mutation returned invalid or unavailable receipt facts.";
            T candidate;
            if (operation == LlmContextCacheMutation.UpdateExpiry)
            {
                string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
                Fence();
                using JsonDocument document = JsonDocument.Parse(body);
                candidate = parseUpdate!(document.RootElement) ?? throw new JsonException();
            }
            else candidate = deletionReceipt!(alreadyAbsent);
            CacheMutationEvidence.Validate(operation, reference, candidate as LlmContextCache, candidate as LlmContextCacheDeleteResult);
            observed = candidate;
            Fence();
        }
        catch (Exception error)
        {
            // The second admission fence can fail without invoking the effectful send.
            if (!dispatched) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            canceled = error is OperationCanceledException;
            failure = ReferenceEquals(error, ownedStatusFailure) ? error : SafeMutationFailure(error, category);
        }
        if (response is not null)
        {
            try { response.Dispose(); }
            catch (Exception error)
            {
                Exception cleanup = LlmResourcePreparation.AsCleanupFailure(
                    SafeMutationFailure(error, "Google cache mutation response cleanup failed."), refusedOwner: response);
                failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
                canceled |= error is OperationCanceledException;
            }
        }
        try { Fence(); }
        catch (Exception error)
        {
            canceled |= error is OperationCanceledException;
            failure ??= SafeMutationFailure(error, "Google cache mutation was interrupted before publication.");
        }
        if (failure is not null)
        {
            var update = observed as LlmContextCache;
            var deletion = observed as LlmContextCacheDeleteResult;
            if (canceled)
                throw new LlmContextCacheMutationCancelledException(operation, reference, cancellationToken, failure, requestId, update, deletion);
            throw new LlmContextCacheMutationUncertainException(operation, reference, failure, requestId, update, deletion);
        }
        return observed!;

        Exception SafeMutationFailure(Exception error, string safeCategory) =>
            error is OperationCanceledException && status is not null
                ? new OperationCanceledException("Google cache mutation was interrupted.",
                    new LlmProviderException("Google cache mutation response observed before interruption.",
                        transport, status, requestId: requestId, retryAfter: retryAfter), cancellationToken)
                : SafeFailure(error, safeCategory, transport, status, requestId, retryAfter,
                    cancellationToken, "Google cache mutation was interrupted.");
    }

    [SuppressMessage("Design", "CA1031", Justification = "The provider boundary sanitizes failures and attempts owned response cleanup for every failure.")]
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
        void Fence()
        {
            cancellationToken.ThrowIfCancellationRequested();
            ownerFence(cancellationToken);
            consumption.Fence();
        }
        Fence();
        HttpResponseMessage? response = null;
        T? observed = default;
        string? requestId = null;
        HttpStatusCode? status = null;
        TimeSpan? retryAfter = null;
        Exception? failure = null;
        LlmProviderException? ownedStatusFailure = null;
        bool canceled = false;
        string category = "Google cache read transport failed.";
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            requestId = ProviderHttp.ReadRequestId(response);
            retryAfter = response.Headers.RetryAfter?.Delta;
            Fence();
            if (!response.IsSuccessStatusCode)
            {
                ownedStatusFailure = new LlmProviderException("Google rejected cache reading.", transport, status,
                    requestId: requestId, retryAfter: retryAfter);
                throw ownedStatusFailure;
            }
            category = "Google cache read returned invalid or unavailable resource facts.";
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
            Fence();
            using JsonDocument document = JsonDocument.Parse(body);
            observed = parse(document.RootElement);
            if (observed is null) throw new JsonException();
            Fence();
        }
        catch (Exception error)
        {
            canceled = error is OperationCanceledException;
            failure = ReferenceEquals(error, ownedStatusFailure) ? error : SafeReadFailure(error, category);
        }
        if (response is not null)
        {
            try { response.Dispose(); }
            catch (Exception error)
            {
                Exception cleanup = LlmResourcePreparation.AsCleanupFailure(
                    SafeReadFailure(error, "Google cache read response cleanup failed."), refusedOwner: response);
                failure = failure is null ? cleanup : new AggregateException(failure, cleanup);
                canceled |= error is OperationCanceledException;
            }
        }
        try { Fence(); }
        catch (Exception error)
        {
            canceled |= error is OperationCanceledException;
            failure ??= SafeReadFailure(error, "Google cache read was interrupted before publication.");
        }
        if (failure is not null)
        {
            if (canceled && failure is not OperationCanceledException)
                throw new OperationCanceledException("Google cache read was interrupted.", failure, cancellationToken);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return observed!;

        Exception SafeReadFailure(Exception error, string safeCategory) => SafeFailure(error, safeCategory,
            transport, status, requestId, retryAfter, cancellationToken, "Google cache read was interrupted.");
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
                Exception cleanup = LlmResourcePreparation.AsCleanupFailure(
                    SafeFailure(error, "Google cache create response cleanup failed.",
                        transport, status, requestId, retryAfter, cancellationToken), refusedOwner: response);
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
        HttpStatusCode? status, string? requestId, TimeSpan? retryAfter, CancellationToken token,
        string cancellationMessage = "Google cache creation was interrupted.")
    {
        if (error is OperationCanceledException)
            return new OperationCanceledException(cancellationMessage, token);
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
