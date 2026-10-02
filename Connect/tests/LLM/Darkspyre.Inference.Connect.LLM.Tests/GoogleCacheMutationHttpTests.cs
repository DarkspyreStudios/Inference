using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GoogleCacheMutationHttpTests
{
    private const string ReceiptBody = "{\"name\":\"cachedContents/id\",\"model\":\"models/m\",\"expireTime\":\"2030-01-01T00:00:00Z\"}";
    private const string Secret = "untrusted-token-private-cache-prefix";
    private static LlmExplicitCacheReference Reference() => new("cachedContents/id", "m", "scope", DateTimeOffset.UnixEpoch);
    private static LlmContextCache Parse(JsonElement root) => GoogleCacheData.Receipt(root, "scope", name => Assert.Equal("cachedContents/id", name), model => { Assert.Equal("models/m", model); return "m"; }, false);

    [Fact]
    public async Task UpdateUsesPatchAndCanonicalReceiptWithoutDisposingBorrowedRequest()
    {
        using var content = new TrackedContent(ReceiptBody);
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        using var requestBody = new TrackedContent("{\"expireTime\":\"2030-01-01T00:00:00Z\"}");
        using var request = new HttpRequestMessage(HttpMethod.Patch, "https://generativelanguage.googleapis.com/v1beta/cachedContents/id") { Content = requestBody };
        LlmContextCache result = await Update(http, request, Reference(), Parse, _ => { });
        Assert.Equal("cachedContents/id", result.Reference.ResourceName);
        Assert.Equal(DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), result.ExpiresAt);
        Assert.Equal(1, handler.Calls); Assert.Equal(1, content.Disposals);
        Assert.Equal(0, requestBody.Disposals); Assert.Equal(0, handler.Disposals);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.NotFound, true)]
    public async Task DeleteUsesOnlyObservedAcknowledgementAndScopedNotFound(HttpStatusCode status, bool absent)
    {
        using var content = new TrackedContent("untrusted response", forbidRead: true);
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        });
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "https://generativelanguage.googleapis.com/v1beta/cachedContents/id");
        var reference = Reference();
        int authorizations = 0;
        var trace = new List<string>();
        LlmContextCacheDeleteResult result = await Delete(http, request, reference, selected =>
        {
            Assert.Same(reference, selected); authorizations++; trace.Add("authorize"); return true;
        }, _ => trace.Add("fence"));
        Assert.Equal(absent, result.AlreadyAbsent);
        Assert.Equal(absent ? 1 : 0, authorizations);
        if (absent)
        {
            int at = trace.IndexOf("authorize");
            Assert.Equal("fence", trace[at - 1]); Assert.Equal("fence", trace[at + 1]);
        }
        Assert.Equal(0, content.Reads); Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task UnverifiedNotFoundIsUncertainNotAlreadyAbsent()
    {
        using var content = new TrackedContent("untrusted response", forbidRead: true);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = content }));
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "https://generativelanguage.googleapis.com/v1beta/cachedContents/id");
        var reference = Reference();
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(() => Delete(http, request, reference, _ => false, _ => { }));
        Assert.Same(reference, error.Reference); Assert.Null(error.ObservedDelete);
        Assert.Equal(HttpStatusCode.NotFound, Assert.IsType<LlmProviderException>(error.InnerException).StatusCode);
        Assert.Equal(0, content.Reads); Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task WrongMethodRefusesBeforeDispatch()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not dispatch."));
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/cachedContents/id");
        await Assert.ThrowsAsync<ArgumentException>(() => Update(http, request, Reference(), Parse, _ => { }));
        await Assert.ThrowsAsync<ArgumentException>(() => Delete(http, request, Reference(), _ => true, _ => { }));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.BadRequest)]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(false, HttpStatusCode.NotFound)]
    [InlineData(false, HttpStatusCode.TooManyRequests)]
    [InlineData(false, HttpStatusCode.InternalServerError)]
    [InlineData(true, HttpStatusCode.BadRequest)]
    [InlineData(true, HttpStatusCode.Unauthorized)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.NotFound)]
    [InlineData(true, HttpStatusCode.TooManyRequests)]
    [InlineData(true, HttpStatusCode.InternalServerError)]
    public async Task NonSuccessIsUncertainWithoutRetryOrBodyRead(bool update, HttpStatusCode status)
    {
        var content = new TrackedContent(Secret, forbidRead: true);
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = content };
            response.Headers.TryAddWithoutValidation("x-request-id", "actual-request");
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var request = Request(update);
        int authorizations = 0;
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(() => Mutate(update, http, request, Parse,
            _ => { authorizations++; return false; }, _ => { }));
        Assert.Equal(update ? LlmContextCacheMutation.UpdateExpiry : LlmContextCacheMutation.Delete, error.Operation);
        Assert.Null(error.ObservedDelete); Assert.Null(error.ObservedUpdate);
        var provider = Assert.IsType<LlmProviderException>(error.InnerException);
        Assert.Equal(status, provider.StatusCode); Assert.Equal("actual-request", error.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(2), provider.RetryAfter);
        Assert.Equal(!update && status == HttpStatusCode.NotFound ? 1 : 0, authorizations);
        Assert.Equal(0, content.Reads); Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Theory]
    [InlineData(true, "send")]
    [InlineData(false, "send")]
    [InlineData(true, "read")]
    [InlineData(true, "parse")]
    [InlineData(false, "authorize")]
    [InlineData(true, "cleanup")]
    [InlineData(false, "cleanup")]
    public async Task UntrustedFailureGraphsAreSanitized(bool update, string boundary)
    {
        Exception malicious = new LlmProviderException(Secret, LlmTransport.GoogleGeminiApi,
            innerException: new InvalidOperationException(Secret));
        var content = new TrackedContent(ReceiptBody, cleanup: boundary == "cleanup" ? () => throw malicious : null,
            beforeRead: boundary == "read" ? () => throw malicious : null);
        using var handler = new Handler((_, _) => boundary == "send" ? throw malicious
            : Task.FromResult(new HttpResponseMessage(boundary == "authorize" ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        using var request = Request(update);
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(() => Mutate(update, http, request,
            root => boundary == "parse" ? throw malicious : Parse(root), _ => boundary == "authorize" ? throw malicious : true, _ => { }));
        Assert.IsType<LlmProviderException>(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Equal(boundary == "send" ? 0 : 1, content.Disposals);
        Assert.Equal(1, handler.Calls); Assert.Equal(0, handler.Disposals);
        if (boundary == "cleanup")
        {
            if (update) Assert.NotNull(error.ObservedUpdate);
            else Assert.False(error.ObservedDelete!.AlreadyAbsent);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IndependentOperationAndCleanupFailuresRemainSafeFacts(bool update, bool cancellation)
    {
        var safeToken = new CancellationTokenSource();
        using var caller = safeToken;
        Exception operationFailure = cancellation ? new OperationCanceledException(Secret, caller.Token) : new InvalidOperationException(Secret);
        var content = new TrackedContent(ReceiptBody, cleanup: () => throw new InvalidOperationException(Secret));
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(update ? HttpStatusCode.OK : HttpStatusCode.NotFound) { Content = content }));
        using var http = new HttpClient(handler);
        using var request = Request(update);
        Exception error = cancellation
            ? await Assert.ThrowsAsync<LlmContextCacheMutationCancelledException>(() => Mutate(update, http, request,
                _ => throw operationFailure, _ => throw operationFailure, _ => { }, caller.Token))
            : await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(() => Mutate(update, http, request,
                _ => throw operationFailure, _ => throw operationFailure, _ => { }, caller.Token));
        var failures = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Equal(2, failures.InnerExceptions.Count);
        Assert.Contains("response cleanup failed", failures.InnerExceptions[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Calls);
        if (cancellation) Assert.Equal(caller.Token, Assert.IsType<LlmContextCacheMutationCancelledException>(error).CancellationToken);
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("model")]
    [InlineData("scope")]
    [InlineData("expiry")]
    [InlineData("null")]
    public async Task InvalidPositiveUpdateCallbackOutputCannotPublishOrBecomeEvidence(string mismatch)
    {
        var content = new TrackedContent(ReceiptBody);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        using var request = Request(true);
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(() => Update(http, request, Reference(), root =>
        {
            var receipt = Parse(root);
            return mismatch switch
            {
                "resource" => receipt with { Reference = receipt.Reference with { ResourceName = Secret } },
                "model" => receipt with { Reference = receipt.Reference with { ModelId = Secret } },
                "scope" => receipt with { Reference = receipt.Reference with { ScopeId = Secret } },
                "expiry" => receipt with { Reference = receipt.Reference with { ExpiresAt = DateTimeOffset.UnixEpoch } },
                _ => null!
            };
        }, _ => { }));
        Assert.Null(error.ObservedUpdate); Assert.Null(error.ObservedDelete);
        Assert.DoesNotContain(Secret, error.ToString()); Assert.Equal(1, content.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndOwnerFenceBeforeDispatchAreOrdinary(bool update)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send."));
        using var http = new HttpClient(handler);
        using var request = Request(update);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Mutate(update, http, request, Parse, _ => true, _ => { }, cancellation.Token));
        Assert.IsNotType<LlmContextCacheMutationCancelledException>(error);
        int fences = 0;
        var original = new OperationCanceledException("Owner fence stopped admission.");
        error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Mutate(update, http, request, Parse, _ => true,
            _ => { if (++fences == 2) throw original; }));
        Assert.Same(original, error); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallbackRetirementAndCleanupRetirementRefuseLateSuccess(bool update)
    {
        bool retiring = false;
        var content = new TrackedContent(ReceiptBody, cleanup: !update ? () => retiring = true : null);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        using var request = Request(update);
        var error = await Assert.ThrowsAsync<LlmContextCacheMutationCancelledException>(() => Mutate(update, http, request,
            root => { var receipt = Parse(root); retiring = true; return receipt; }, _ => true,
            token => { if (retiring) throw new OperationCanceledException("Owner retired.", token); }));
        if (update) Assert.NotNull(error.ObservedUpdate);
        else Assert.False(error.ObservedDelete!.AlreadyAbsent);
        Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Calls);
    }

    private static Task<LlmContextCache> Update(HttpClient http, HttpRequestMessage request, LlmExplicitCacheReference reference,
        Func<JsonElement, LlmContextCache> parse, Action<CancellationToken> fence, CancellationToken token = default) =>
        GoogleCacheHttp.UpdateExpiryAsync(http, request, LlmTransport.GoogleGeminiApi, reference, parse, fence, token);

    private static Task<LlmContextCacheDeleteResult> Delete(HttpClient http, HttpRequestMessage request, LlmExplicitCacheReference reference,
        Func<LlmExplicitCacheReference, bool> authorize, Action<CancellationToken> fence, CancellationToken token = default) =>
        GoogleCacheHttp.DeleteAsync(http, request, LlmTransport.GoogleGeminiApi, reference, authorize, fence, token);

    private static async Task<object> Mutate(bool update, HttpClient http, HttpRequestMessage request,
        Func<JsonElement, LlmContextCache> parse, Func<LlmExplicitCacheReference, bool> authorize,
        Action<CancellationToken> fence, CancellationToken token = default)
    {
        if (update) return await Update(http, request, Reference(), parse, fence, token);
        return await Delete(http, request, Reference(), authorize, fence, token);
    }

    private static HttpRequestMessage Request(bool update) => new(update ? HttpMethod.Patch : HttpMethod.Delete,
        "https://generativelanguage.googleapis.com/v1beta/cachedContents/id");

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class TrackedContent(string body, bool forbidRead = false, Action? cleanup = null, Action? beforeRead = null,
        TaskCompletionSource? entered = null, TaskCompletionSource? release = null) : HttpContent
    {
        internal int Reads { get; private set; }
        internal int Disposals { get; private set; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Reads++;
            if (forbidRead) throw new InvalidOperationException("Response body must not be read.");
            beforeRead?.Invoke();
            if (entered is not null) { entered.TrySetResult(); await release!.Task; }
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body));
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) { Disposals++; cleanup?.Invoke(); } base.Dispose(disposing); }
    }
}
