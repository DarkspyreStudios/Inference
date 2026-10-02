using System.Net;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GeminiCacheLifecycleTests
{
    private static readonly DateTimeOffset Expiry = DateTimeOffset.UtcNow.AddHours(2);
    private const string Model = "gemini-3-flash-preview";

    [Fact]
    public async Task PublicBindingCreatesGetsListsUpdatesAndDeletesThroughSelectedSnapshot()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var source = new Source();
        await using var facade = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        ILlmContextCacheClient cache = await facade.BindContextCacheAsync("selected");
        Assert.Empty(handler.Requests);
        LlmContextCache created = await cache.CreateAsync(new()
        {
            ModelId = Model,
            Messages = [new("user", "immutable prefix")],
            Correlation = new("operation", "reconciliation"),
            TimeToLive = TimeSpan.FromMinutes(10)
        });
        Assert.Equal(Expiry, created.ExpiresAt);
        Assert.Equal("cachedContents/native", created.Reference.ResourceName);
        Assert.NotEmpty(created.Reference.ScopeId);
        LlmContextCache fetched = await cache.GetAsync(created.Reference);
        Assert.Equal(created.Reference, fetched.Reference);
        LlmContextCachePage page = await cache.ListAsync(new(7, "literal+token"));
        Assert.Single(page.Items);
        Assert.Equal("next", page.NextPageToken);
        handler.Expiry = Expiry.AddHours(1);
        LlmContextCache updated = await cache.UpdateExpiryAsync(created.Reference, handler.Expiry);
        Assert.Equal(handler.Expiry, updated.ExpiresAt);
        Assert.False((await cache.DeleteAsync(updated.Reference)).AlreadyAbsent);
        Assert.Equal(6, source.Reads);
        Assert.Equal(0, source.Saves);
        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("private-key", request.Key));
    }

    [Fact]
    public async Task KnownCopiedReceiptEnablesCanonicalCachedGenerationAndExactCountingWithoutGet()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var source = new Source();
        var adapter = new GeminiApiLlmClient(http);
        source.Value = source.Value with { CapabilityOverrides = new() { SupportsStreaming = false } };
        await using var facade = new LlmProviderService(source, [adapter]);
        ILlmContextCacheClient cache = await facade.BindContextCacheAsync("selected");
        LlmContextCache receipt = await cache.GetAsync(new("cachedContents/native", Model, "imported"));
        var request = new LlmChatRequest
        {
            ModelId = Model,
            ExplicitCache = receipt.Reference with { },
            Messages = [new("user", "uncached suffix")]
        };
        var events = new List<LlmStreamEvent>();
        await foreach (LlmStreamEvent item in facade.StreamAsync("selected", request)) events.Add(item);
        _ = await facade.CountInputTokensAsync("selected", request);
        Assert.Equal(1, handler.Requests.Count(request => request.Method == HttpMethod.Get));
        Request generation = handler.Requests.Single(request => request.Path.EndsWith(":generateContent", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(generation.Body!);
        Assert.Equal("cachedContents/native", body.RootElement.GetProperty("cachedContent").GetString());
        Assert.DoesNotContain("immutable prefix", generation.Body!, StringComparison.Ordinal);
        Request count = handler.Requests.Single(request => request.Path.EndsWith(":countTokens", StringComparison.Ordinal));
        using var counting = JsonDocument.Parse(count.Body!);
        Assert.Equal("cachedContents/native", counting.RootElement.GetProperty("generateContentRequest").GetProperty("cachedContent").GetString());
        Assert.False(adapter.GetCapabilities(source.Value, Model).SupportsExplicitCache);
    }

    [Fact]
    public async Task CachedSuffixConflictsRefuseBeforeProviderEffects()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var source = new Source();
        await using var facade = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        ILlmContextCacheClient cache = await facade.BindContextCacheAsync("selected");
        LlmContextCache receipt = await cache.GetAsync(new("cachedContents/native", Model, "imported"));
        int effects = handler.Requests.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => facade.CountInputTokensAsync("selected", new()
        {
            ModelId = Model, ExplicitCache = receipt.Reference, Instructions = "conflicting system",
            Messages = [new("user", "suffix")]
        }));
        Assert.Equal(effects, handler.Requests.Count);
    }

    private sealed class Source : ILlmConnectionSource
    {
        internal LlmConnection Value { get; set; } = new()
        {
            Version = "v1", Transport = LlmTransport.GoogleGeminiApi,
            Endpoint = GeminiApiLlmClient.DefaultEndpoint,
            Credential = new LlmCredential { ApiKey = "private-key" }.Serialize()
        };
        internal int Reads { get; private set; }
        internal int Saves { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        { Assert.Equal("selected", connectionId); Reads++; return Task.FromResult<LlmConnection?>(Value); }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Saves++; throw new InvalidOperationException("Gemini does not refresh API keys."); }
    }

    private sealed record Request(HttpMethod Method, string Path, string? Body, string? Key);

    private sealed class Handler : HttpMessageHandler
    {
        internal List<Request> Requests { get; } = [];
        internal DateTimeOffset Expiry { get; set; } = GeminiCacheLifecycleTests.Expiry;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, request.RequestUri!.AbsolutePath, body,
                request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null));
            string receipt = JsonSerializer.Serialize(new { name = "cachedContents/native", model = "models/" + Model, expireTime = Expiry.ToString("O"), usageMetadata = new { totalTokenCount = 0 } });
            string response = request.Method == HttpMethod.Delete ? "{}"
                : request.RequestUri.AbsolutePath.EndsWith(":countTokens", StringComparison.Ordinal) ? "{\"totalTokens\":9,\"cachedContentTokenCount\":7}"
                : request.RequestUri.AbsolutePath.EndsWith(":generateContent", StringComparison.Ordinal) ? "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"answer\"}]},\"finishReason\":\"STOP\"}]}"
                : request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/v1beta/cachedContents" ? "{\"cachedContents\":[" + receipt + "],\"nextPageToken\":\"next\"}"
                : receipt;
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
