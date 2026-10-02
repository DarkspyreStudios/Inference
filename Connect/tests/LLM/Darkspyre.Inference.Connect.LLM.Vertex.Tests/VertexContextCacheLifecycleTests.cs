using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexContextCacheLifecycleTests
{
    private const string Name = "projects/123456/locations/global/cachedContents/cache-1";
    private const string Model = "google/gemini-2.5-flash";

    [Fact]
    public async Task StandardBindingIsEffectFreeAndGetUsesConfiguredParent()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var auth = new Authentication(); var source = new Source(Connection());
        await using var facade = new LlmProviderService(source, [new VertexLlmClient(http, auth)]);
        ILlmContextCacheClient caches = await facade.BindContextCacheAsync("connection");
        Assert.Equal(0, auth.Calls); Assert.Empty(handler.Paths);
        LlmContextCache cache = await caches.GetAsync(new(Name, Model, "imported"));
        Assert.Equal(Name, cache.Reference.ResourceName); Assert.Equal(Model, cache.Reference.ModelId);
        Assert.NotEqual("imported", cache.Reference.ScopeId); Assert.Equal(1, auth.Calls);
        Assert.Equal("/v1/projects/project-id/locations/global/cachedContents/cache-1", Assert.Single(handler.Paths));
        Assert.Equal("Bearer controlled", Assert.Single(handler.Authorization));
    }

    [Fact]
    public async Task ListIsOnePageAndPreservesOpaqueContinuation()
    {
        using var handler = new Handler(list: true); using var http = new HttpClient(handler);
        var auth = new Authentication();
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, auth)]);
        ILlmContextCacheClient caches = await facade.BindContextCacheAsync("connection");
        LlmContextCachePage page = await caches.ListAsync(new(2, "a/+%"));
        Assert.Equal(Name, Assert.Single(page.Items).Reference.ResourceName); Assert.Equal("next/+%", page.NextPageToken);
        Assert.Equal("/v1/projects/project-id/locations/global/cachedContents?pageSize=2&pageToken=a%2F%2B%25", Assert.Single(handler.Paths));
        Assert.Equal(1, auth.Calls);
    }

    [Theory]
    [InlineData("models/gemini-2.5-flash")]
    [InlineData("projects/project-id/locations/global/publishers/google/models/gemini-2.5-flash")]
    [InlineData("projects/123456/locations/global/publishers/google/models/gemini-2.5-flash")]
    public async Task DocumentedResponseModelsPreserveCanonicalIdentity(string nativeModel)
    {
        using var handler = new Handler(nativeModel: nativeModel); using var http = new HttpClient(handler);
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, new Authentication())]);
        ILlmContextCacheClient caches = await facade.BindContextCacheAsync("connection");
        Assert.Equal(Model, (await caches.GetAsync(new(Name, Model, "imported"))).Reference.ModelId);
    }

    [Fact]
    public async Task CreateUpdateAndRepeatedDeleteUseOneSnapshotPerOperation()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var auth = new Authentication();
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, auth)]);
        ILlmContextCacheClient caches = await facade.BindContextCacheAsync("connection");
        LlmContextCache created = await caches.CreateAsync(new()
        { ModelId = Model, Messages = [new("user", "prefix")], Instructions = "instruction",
            Correlation = new("operation", "reconcile"), TimeToLive = TimeSpan.FromHours(1) });
        Assert.Equal(1, auth.Calls);
        using (JsonDocument body = JsonDocument.Parse(handler.Bodies[0]!))
        {
            Assert.Equal("projects/project-id/locations/global/publishers/google/models/gemini-2.5-flash", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("prefix", body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal("instruction", body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal("3600s", body.RootElement.GetProperty("ttl").GetString());
        }
        DateTimeOffset expiry = DateTimeOffset.Parse("2051-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        LlmContextCache updated = await caches.UpdateExpiryAsync(created.Reference, expiry);
        Assert.Equal(expiry, updated.ExpiresAt);
        using (JsonDocument body = JsonDocument.Parse(handler.Bodies[1]!))
            Assert.Single(body.RootElement.EnumerateObject());
        Assert.False((await caches.DeleteAsync(updated.Reference)).AlreadyAbsent);
        Assert.True((await caches.DeleteAsync(updated.Reference)).AlreadyAbsent);
        Assert.Equal(4, auth.Calls); Assert.Equal(4, handler.Paths.Count);
        Assert.Equal("PATCH", handler.Methods[1]); Assert.Contains("updateMask=expireTime", handler.Paths[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CachedGenerationUsesSameAcquiredAuthorizationAndReportsActualUsage()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var auth = new Authentication();
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, auth)]);
        var caches = await facade.BindContextCacheAsync("connection");
        LlmContextCache observed = await caches.GetAsync(new(Name, Model, "imported"));
        LlmChatResult result = await facade.CompleteAsync("connection", new()
        { ModelId = Model, ExplicitCache = observed.Reference, Messages = [new("user", "suffix")] });
        Assert.Equal("done", result.Text); Assert.Equal(5, result.Usage!.CachedInputTokens);
        Assert.Equal(2, auth.Calls); Assert.Equal(2, handler.Paths.Count);
        using JsonDocument body = JsonDocument.Parse(handler.Bodies[1]!);
        Assert.Equal(Name, body.RootElement.GetProperty("cachedContent").GetString());
        Assert.False(body.RootElement.TryGetProperty("systemInstruction", out _));
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task UnknownTupleRefusesBeforeAuthenticationAndHttp()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var auth = new Authentication();
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, auth)]);
        await Assert.ThrowsAsync<ArgumentException>(() => facade.CompleteAsync("connection", new()
        { ModelId = Model, ExplicitCache = new(Name, Model, "invented"), Messages = [new("user", "suffix")] }));
        Assert.Equal(0, auth.Calls); Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData("projects/123456/locations/us-central1/cachedContents/cache-1", "project-id")]
    [InlineData(Name, "654321")]
    [InlineData("https://aiplatform.googleapis.com/" + Name, "project-id")]
    public async Task KnownRouteContradictionsRefuseBeforeAuthentication(string name, string project)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var auth = new Authentication();
        await using var facade = new LlmProviderService(new Source(Connection() with { Vertex = new() { ProjectId = project, Location = "global" } }), [new VertexLlmClient(http, auth)]);
        var caches = await facade.BindContextCacheAsync("connection");
        await Assert.ThrowsAsync<ArgumentException>(() => caches.GetAsync(new(name, Model, "imported")));
        Assert.Equal(0, auth.Calls); Assert.Empty(handler.Paths);
    }

    [Theory]
    [InlineData("instructions")]
    [InlineData("system")]
    [InlineData("tools")]
    [InlineData("choice")]
    public async Task CachedSuffixConflictsRefuseBeforeAuthenticationOrGet(string conflict)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var auth = new Authentication();
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, auth)]);
        var caches = await facade.BindContextCacheAsync("connection");
        var observed = await caches.GetAsync(new(Name, Model, "imported"));
        var request = new LlmChatRequest { ModelId = Model, ExplicitCache = observed.Reference, Messages = [new("user", "suffix")] };
        request = conflict switch
        {
            "instructions" => request with { Instructions = "conflict" },
            "system" => request with { Messages = [new("system", "conflict"), new("user", "suffix")] },
            "tools" => request with { Tools = [new LlmToolDefinition { Name = "tool", InputSchema = JsonElement.Parse("{\"type\":\"object\"}") }] },
            _ => request with { ToolChoice = new() { Mode = LlmToolChoiceMode.None } }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => facade.CompleteAsync("connection", request));
        Assert.Equal(1, auth.Calls); Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task ChangedAuthenticationEvidenceRevalidatesKnownReceiptWithoutMakingHashAuthority()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var auth = new Authentication();
        await using var facade = new LlmProviderService(new Source(Connection()), [new VertexLlmClient(http, auth)]);
        var caches = await facade.BindContextCacheAsync("connection");
        var observed = await caches.GetAsync(new(Name, Model, "imported"));
        auth.Scope = "replacement";
        _ = await facade.CompleteAsync("connection", new() { ModelId = Model, ExplicitCache = observed.Reference, Messages = [new("user", "suffix")] });
        Assert.Equal(2, auth.Calls); Assert.Equal(3, handler.Paths.Count);
        Assert.EndsWith("/cachedContents/cache-1", handler.Paths[1], StringComparison.Ordinal);
        Assert.EndsWith(":generateContent", handler.Paths[2], StringComparison.Ordinal);
    }

    private static LlmConnection Connection() => new()
    {
        Transport = LlmTransport.GoogleVertexAi, Version = "1", Endpoint = "https://aiplatform.googleapis.com",
        Vertex = new() { ProjectId = "project-id", Location = "global" },
        CapabilityOverrides = new() { SupportsModelListing = false, SupportsStreaming = false }
    };

    private sealed class Source(LlmConnection connection) : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(connection);
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }

    private sealed class Authentication : IVertexAuthentication
    {
        internal int Calls { get; private set; }
        internal string Scope { get; set; } = "controlled-source";
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new VertexAccess("controlled", Scope)); }
    }

    private sealed class Handler(bool list = false, string nativeModel = "models/gemini-2.5-flash") : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal List<string?> Authorization { get; } = [];
        internal List<string?> Bodies { get; } = [];
        internal List<string> Methods { get; } = [];
        private bool _deleted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery); Authorization.Add(request.Headers.Authorization?.ToString());
            Methods.Add(request.Method.Method);
            string? sent = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(sent);
            if (request.RequestUri.AbsolutePath.EndsWith(":generateContent", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"done\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":7,\"cachedContentTokenCount\":5,\"candidatesTokenCount\":2,\"totalTokenCount\":9}}") };
            if (request.Method == HttpMethod.Delete)
            { bool already = _deleted; _deleted = true; return new(already ? HttpStatusCode.NotFound : HttpStatusCode.OK); }
            string expiry = "2050-01-01T00:00:00Z";
            if (request.Method == HttpMethod.Patch)
            { using JsonDocument body = JsonDocument.Parse(sent!); expiry = body.RootElement.GetProperty("expireTime").GetString()!; }
            string item = "{\"name\":\"" + Name + "\",\"model\":\"" + nativeModel + "\",\"expireTime\":\"" + expiry + "\"}";
            return new(HttpStatusCode.OK) { Content = new StringContent(list ? "{\"cachedContents\":[" + item + "],\"nextPageToken\":\"next/+%\"}" : item) };
        }
    }
}
