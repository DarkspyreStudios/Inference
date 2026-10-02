using System.Net;
using System.Reflection;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GeminiCacheReadsTests
{
    private const string Receipt = """{"name":"cachedContents/id","model":"models/m","expireTime":"2030-01-01T00:00:00Z"}""";
    private const string Secret = "private-key-prefix";

    [Fact]
    public async Task ExplicitGetUsesFixedRouteAndRebindsImportedScopeFromActualReceipt()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/cachedContents/id", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("selected-key", request.Headers.GetValues("x-goog-api-key").Single());
            Assert.Null(request.Content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Receipt) });
        });
        using var http = new HttpClient(handler);
        var result = await Get(http, Connection(), Reference(), "current-scope", _ => { }, CancellationToken.None);
        Assert.Equal("current-scope", result.Reference.ScopeId);
        Assert.Equal("cachedContents/id", result.Reference.ResourceName);
        Assert.Equal("m", result.Reference.ModelId);
        Assert.Equal(DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), result.ExpiresAt);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(0, handler.Disposals);
    }

    [Fact]
    public async Task ListReadsOnePageWithExplicitSizeAndOpaqueToken()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/cachedContents?pageSize=3&pageToken=a%2B%2F%25%26", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"cachedContents\":[" + Receipt + "],\"nextPageToken\":\"next\"}") });
        });
        using var http = new HttpClient(handler);
        var page = await List(http, Connection(), new(3, "a+/%&"), "current-scope", _ => { }, CancellationToken.None);
        Assert.Equal("current-scope", Assert.Single(page.Items).Reference.ScopeId);
        Assert.Equal("next", page.NextPageToken);
        Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task CustomEndpointIsRefusedBeforeHttp()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No send."));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<NotSupportedException>(() => Get(http, Connection() with { Endpoint = "https://custom.example" },
            Reference(), "current-scope", _ => { }, CancellationToken.None));
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cachedContents/")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/cachedContents/id")]
    [InlineData("projects/p/locations/l/cachedContents/id")]
    [InlineData("cachedContents/a/b")]
    [InlineData("cachedContents/.")]
    [InlineData("cachedContents/..")]
    [InlineData("cachedContents/a\\b")]
    [InlineData("cachedContents/a\nsecret")]
    public async Task InvalidResourceNamesRefuseBeforeSend(string name)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No send."));
        using var http = new HttpClient(handler);
        var reference = Reference() with { ResourceName = name.Length == 0 ? "invalid" : name };
        await Assert.ThrowsAsync<ArgumentException>(() => Get(http, Connection(), reference, "scope", _ => { }, CancellationToken.None));
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData("models/m")]
    [InlineData("m/x")]
    [InlineData("m?key=secret")]
    [InlineData("M")]
    public async Task InvalidModelNamesRefuseBeforeSend(string model)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No send."));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Get(http, Connection(), Reference() with { ModelId = model },
            "scope", _ => { }, CancellationToken.None));
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData("a+%?#&", "a%2B%25%3F%23%26")]
    [InlineData("%2F", "%252F")]
    [InlineData("unicode-\u03b1", "unicode-%CE%B1")]
    public async Task OpaqueIdsRemainDataEncodedOnce(string id, string encoded)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/cachedContents/" + encoded, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { name = "cachedContents/" + id, model = "models/m", expireTime = "2030-01-01T00:00:00Z" })) });
        });
        using var http = new HttpClient(handler);
        var result = await Get(http, Connection() with { Endpoint = GeminiApiLlmClient.DefaultEndpoint + "///" },
            Reference() with { ResourceName = "cachedContents/" + id }, "scope", _ => { }, CancellationToken.None);
        Assert.Equal("cachedContents/" + id, result.Reference.ResourceName);
    }

    [Theory]
    [InlineData("wrong-name")]
    [InlineData("wrong-model")]
    [InlineData("url-name")]
    [InlineData("bare-model")]
    [InlineData("invalid-expiry")]
    public async Task PositiveGetCannotPublishContradictoryReceipt(string field)
    {
        string body = field switch
        {
            "wrong-name" => Receipt.Replace("cachedContents/id", "cachedContents/other", StringComparison.Ordinal),
            "wrong-model" => Receipt.Replace("models/m", "models/other", StringComparison.Ordinal),
            "url-name" => Receipt.Replace("cachedContents/id", "https://evil.example/" + Secret, StringComparison.Ordinal),
            "bare-model" => Receipt.Replace("models/m", Secret, StringComparison.Ordinal),
            _ => Receipt.Replace("2030-01-01T00:00:00Z", Secret, StringComparison.Ordinal)
        };
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Get(http, Connection(), Reference(), "scope", _ => { }, CancellationToken.None));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidAuthAndForeignConnectionControlsRefuseBeforeSend(bool list)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No send."));
        using var http = new HttpClient(handler);
        LlmConnection[] invalid =
        [
            Connection() with { Transport = LlmTransport.OpenAiResponses },
            Connection() with { Endpoint = GeminiApiLlmClient.DefaultEndpoint + "/v1beta" },
            Connection() with { Endpoint = GeminiApiLlmClient.DefaultEndpoint + "?key=" + Secret },
            Connection() with { AdditionalHeaders = new Dictionary<string,string> { ["x-extra"] = "value" } },
            Connection() with { Credential = new LlmCredential { ApiKey = "k", OAuth = new() { AccessToken = "a" } }.Serialize() },
            Connection() with { Credential = null },
            Connection() with { Credential = new LlmCredential { ApiKey = Secret + "\r\nx-extra: value" }.Serialize() }
        ];
        foreach (LlmConnection connection in invalid)
        {
            Exception error = await Assert.ThrowsAnyAsync<Exception>(() => Read(list, http, connection, _ => { }, CancellationToken.None));
            Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArgumentsCancellationAndOwnerAdmissionAreEffectFree(bool list)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No send."));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentNullException>(() => Read(list, null!, Connection(), _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Read(list, http, null!, _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Read(list, http, Connection(), null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => list
            ? List(http, Connection(), new(1), " ", _ => { }, CancellationToken.None)
            : Get(http, Connection(), Reference(), " ", _ => { }, CancellationToken.None));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(list, http, Connection(), _ => { }, canceled.Token));
        var original = new OperationCanceledException("owner retired");
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(list, http, Connection(), _ => throw original, CancellationToken.None));
        Assert.Same(original, error);
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task ListPreservesDocumentedPageBoundAndEmptyPage(int size)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("?pageSize=" + size.ToString(System.Globalization.CultureInfo.InvariantCulture), request.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        });
        using var http = new HttpClient(handler);
        var page = await List(http, Connection(), new(size), "scope", _ => { }, CancellationToken.None);
        Assert.Empty(page.Items); Assert.Null(page.NextPageToken);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => List(http, Connection(), new(1001), "scope", _ => { }, CancellationToken.None));
        Assert.Equal(1, handler.Sends);
    }

    private static async Task<object> Read(bool list, HttpClient http, LlmConnection connection, Action<CancellationToken> fence, CancellationToken token) =>
        list ? await List(http, connection, new(1), "scope", fence, token) : await Get(http, connection, Reference(), "scope", fence, token);

    private static Task<LlmContextCache> Get(HttpClient http, LlmConnection connection, LlmExplicitCacheReference reference,
        string scope, Action<CancellationToken> fence, CancellationToken token) =>
        GeminiCacheReads.GetAsync(http, connection, reference, scope, fence, token);

    private static Task<LlmContextCachePage> List(HttpClient http, LlmConnection connection, LlmContextCacheList request,
        string scope, Action<CancellationToken> fence, CancellationToken token) =>
        GeminiCacheReads.ListAsync(http, connection, request, scope, fence, token);

    private static LlmConnection Connection() => new()
    {
        Version = "v1", Transport = LlmTransport.GoogleGeminiApi,
        Endpoint = GeminiApiLlmClient.DefaultEndpoint, Credential = new LlmCredential { ApiKey = "selected-key" }.Serialize()
    };

    private static LlmExplicitCacheReference Reference() => new("cachedContents/id", "m", "imported-scope");

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Sends { get; private set; }
        internal int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Sends++; return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
}
