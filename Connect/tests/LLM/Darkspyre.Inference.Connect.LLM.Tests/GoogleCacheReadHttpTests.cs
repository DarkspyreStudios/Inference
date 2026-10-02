using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GoogleCacheReadHttpTests
{
    private const string Secret = "cache-token-private-prefix";
    private const string ReceiptBody = "{\"name\":\"cachedContents/id\",\"model\":\"models/m\",\"expireTime\":\"2030-01-01T00:00:00Z\"}";

    [Theory]
    [InlineData(false, "transport")]
    [InlineData(true, "transport")]
    [InlineData(false, "parse")]
    [InlineData(true, "parse")]
    [InlineData(false, "cleanup")]
    [InlineData(true, "cleanup")]
    public async Task UntrustedReadFailuresAreSafeOrdinaryProviderFailures(bool list, string boundary)
    {
        var original = new LlmProviderException(Secret, LlmTransport.GoogleGeminiApi,
            HttpStatusCode.Forbidden, providerCode: Secret, sanitizedResponseBody: Secret,
            innerException: new IOException(Secret));
        var content = new Content(ReceiptBody, cleanupFailure: boundary == "cleanup" ? original : null);
        int sends = 0;
        using var handler = new Handler((_, _) =>
        {
            sends++;
            return boundary == "transport" ? Task.FromException<HttpResponseMessage>(original)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        using var request = Request();
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Read(list, http, request,
            _ => { if (boundary == "parse") throw original; }, _ => { }, CancellationToken.None));
        Assert.NotSame(original, error);
        Assert.Null(error.InnerException);
        Assert.Null(error.ProviderCode);
        Assert.Null(error.SanitizedResponseBody);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, sends);
        Assert.Equal(boundary == "transport" ? 0 : 1, content.Disposals);
        Assert.Equal(0, handler.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParserTriggeredRetirementCannotPublishLateReceiptOrPage(bool list)
    {
        using var owner = new LlmOperationLifetime();
        var lease = owner.Enter(CancellationToken.None);
        Task? retirement = null;
        var content = new Content(ReceiptBody);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        using var request = Request();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(list, http, request,
                _ => retirement = owner.RetireAsync(), token => Fence(owner, token), lease.Token));
            Assert.IsNotType<LlmContextCacheCreateCancelledException>(error);
            Assert.False(retirement!.IsCompleted);
            Assert.Equal(1, content.Disposals);
        }
        finally
        {
            retirement ??= owner.RetireAsync();
            lease.Dispose();
            await retirement;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulReadBorrowsRequestAndClientAndDisposesOnlyResponse(bool list)
    {
        var content = new Content(ReceiptBody);
        int sends = 0, parses = 0;
        using var handler = new Handler((request, _) =>
        {
            sends++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("selected-key", request.Headers.GetValues("x-goog-api-key").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        using var request = Request();
        request.Headers.Add("x-goog-api-key", "selected-key");
        object result = await Read(list, http, request, _ => parses++, _ => { }, CancellationToken.None);
        if (list) Assert.Equal("cachedContents/id", Assert.Single(Assert.IsType<LlmContextCachePage>(result).Items).Reference.ResourceName);
        else Assert.Equal("cachedContents/id", Assert.IsType<LlmContextCache>(result).Reference.ResourceName);
        Assert.Equal(1, sends);
        Assert.Equal(1, parses);
        Assert.Equal(1, content.Disposals);
        Assert.Equal(0, handler.Disposals);
        Assert.Equal("selected-key", request.Headers.GetValues("x-goog-api-key").Single());
    }

    private static async Task<object> Read(bool list, HttpClient http, HttpRequestMessage request,
        Action<JsonElement> parsed, Action<CancellationToken> fence, CancellationToken token)
    {
        if (list) return await GoogleCacheHttp.ListAsync(http, request, LlmTransport.GoogleGeminiApi,
            root => { parsed(root); return new([Parse(root)], null); }, fence, token);
        return await GoogleCacheHttp.GetAsync(http, request, LlmTransport.GoogleGeminiApi,
            root => { parsed(root); return Parse(root); }, fence, token);
    }

    private static LlmContextCache Parse(JsonElement root) => GoogleCacheData.Receipt(root, "scope", _ => { }, _ => "m", vertex: false);
    private static HttpRequestMessage Request() => new(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/cachedContents/id");
    private static void Fence(LlmOperationLifetime owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { owner.ThrowIfStopping(); }
        catch (ObjectDisposedException) { throw new OperationCanceledException("Owned cache read retired.", token); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class Content(string text, Exception? readFailure = null, Exception? cleanupFailure = null) : HttpContent
    {
        internal int Reads { get; private set; }
        internal int Disposals { get; private set; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Reads++;
            if (readFailure is not null) throw readFailure;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
            if (disposing && cleanupFailure is not null) throw cleanupFailure;
        }
    }
}
