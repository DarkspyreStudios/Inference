using System.Net;
using System.Reflection;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GeminiCacheReadsTests
{
    private const string Receipt = """{"name":"cachedContents/id","model":"models/m","expireTime":"2030-01-01T00:00:00Z"}""";

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

    private static Task<LlmContextCache> Get(HttpClient http, LlmConnection connection, LlmExplicitCacheReference reference,
        string scope, Action<CancellationToken> fence, CancellationToken token) =>
        Invoke<LlmContextCache>("GetAsync", http, connection, reference, scope, fence, token);

    private static Task<LlmContextCachePage> List(HttpClient http, LlmConnection connection, LlmContextCacheList request,
        string scope, Action<CancellationToken> fence, CancellationToken token) =>
        Invoke<LlmContextCachePage>("ListAsync", http, connection, request, scope, fence, token);

    private static async Task<T> Invoke<T>(string name, params object[] args)
    {
        Type? type = typeof(ConnectLlmJson).Assembly.GetType("Darkspyre.Inference.Connect.LLM.Internal.GeminiCacheReads");
        Assert.NotNull(type);
        MethodInfo? method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Task<T> task;
        try { task = (Task<T>)method.Invoke(null, args)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        return await task;
    }

    private static LlmConnection Connection() => new()
    {
        Version = "v1", Transport = LlmTransport.GoogleGeminiApi,
        Endpoint = GeminiApiLlmClient.DefaultEndpoint, Credential = "selected-key"
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
