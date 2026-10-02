using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GoogleCacheMutationHttpTests
{
    private const string ReceiptBody = "{\"name\":\"cachedContents/id\",\"model\":\"models/m\",\"expireTime\":\"2030-01-01T00:00:00Z\"}";
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

    private static Task<LlmContextCache> Update(HttpClient http, HttpRequestMessage request, LlmExplicitCacheReference reference,
        Func<JsonElement, LlmContextCache> parse, Action<CancellationToken> fence, CancellationToken token = default) =>
        Invoke<LlmContextCache>("UpdateExpiryAsync", [http, request, LlmTransport.GoogleGeminiApi, reference, parse, fence, token]);

    private static Task<LlmContextCacheDeleteResult> Delete(HttpClient http, HttpRequestMessage request, LlmExplicitCacheReference reference,
        Func<LlmExplicitCacheReference, bool> authorize, Action<CancellationToken> fence, CancellationToken token = default) =>
        Invoke<LlmContextCacheDeleteResult>("DeleteAsync", [http, request, LlmTransport.GoogleGeminiApi, reference, authorize, fence, token]);

    private static Task<T> Invoke<T>(string name, object?[] arguments)
    {
        MethodInfo? method = typeof(GoogleCacheHttp).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        try { return (Task<T>)method.Invoke(null, arguments)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class TrackedContent(string body, bool forbidRead = false) : HttpContent
    {
        internal int Reads { get; private set; }
        internal int Disposals { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Reads++;
            if (forbidRead) throw new InvalidOperationException("Response body must not be read.");
            return stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
}
