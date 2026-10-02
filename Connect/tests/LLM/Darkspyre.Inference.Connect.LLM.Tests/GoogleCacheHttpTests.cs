using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GoogleCacheHttpTests
{
    private const string Secret = "cache-auth-token-that-must-not-escape";
    private const string Prefix = "private cached prefix that must not escape";
    private static readonly LlmContextCacheCreateCorrelation Correlation = new("operation", "reconcile");
    private const string ReceiptBody = "{\"name\":\"cachedContents/id\",\"model\":\"models/m\",\"expireTime\":\"2030-01-01T00:00:00Z\"}";

    [Theory]
    [InlineData("transport")]
    [InlineData("provider")]
    [InlineData("parse")]
    [InlineData("cleanup")]
    public async Task UntrustedFailureGraphsCannotExposeTokenOrPrefix(string boundary)
    {
        var failure = boundary == "provider"
            ? (Exception)new LlmProviderException(Secret + Prefix, LlmTransport.GoogleGeminiApi,
                HttpStatusCode.Forbidden, sanitizedResponseBody: Secret + Prefix,
                innerException: new InvalidOperationException(Secret + Prefix))
            : new HttpRequestException(Secret + Prefix, new IOException(Secret + Prefix));
        int sends = 0;
        using var handler = new Handler((_, _) =>
        {
            sends++;
            if (boundary is "transport" or "provider") return Task.FromException<HttpResponseMessage>(failure);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = boundary == "cleanup" ? new FaultContent(ReceiptBody, failure) : new StringContent(Secret + Prefix)
            });
        });
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/v1beta/cachedContents");
        var error = await Assert.ThrowsAsync<LlmContextCacheCreateUncertainException>(() =>
            GoogleCacheHttp.CreateAsync(http, request, LlmTransport.GoogleGeminiApi, Correlation,
                Parse, _ => { }, CancellationToken.None));
        Assert.Equal(1, sends);
        Assert.Same(Correlation, error.Correlation);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Prefix, error.ToString(), StringComparison.Ordinal);
        Assert.All(Errors(error), item =>
        {
            Assert.DoesNotContain(Secret, item.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Prefix, item.Message, StringComparison.Ordinal);
            if (item is LlmProviderException provider) Assert.Null(provider.SanitizedResponseBody);
        });
    }

    private static IEnumerable<Exception> Errors(Exception root)
    {
        yield return root;
        if (root is AggregateException aggregate)
            foreach (Exception error in aggregate.InnerExceptions)
                foreach (Exception nested in Errors(error)) yield return nested;
        else if (root.InnerException is { } inner)
            foreach (Exception error in Errors(inner)) yield return error;
    }

    private static LlmContextCache Parse(JsonElement root) => GoogleCacheData.Receipt(root, "scope", _ => { }, _ => "m", vertex: false);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class FaultContent(string text, Exception error) : StringContent(text)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) throw error;
        }
    }
}
