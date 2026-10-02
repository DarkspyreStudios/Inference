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
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new VertexAccess("controlled", "controlled-source")); }
    }

    private sealed class Handler(bool list = false, string nativeModel = "models/gemini-2.5-flash") : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal List<string?> Authorization { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery); Authorization.Add(request.Headers.Authorization?.ToString());
            string item = "{\"name\":\"" + Name + "\",\"model\":\"" + nativeModel + "\",\"expireTime\":\"2050-01-01T00:00:00Z\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(list ? "{\"cachedContents\":[" + item + "],\"nextPageToken\":\"next/+%\"}" : item) });
        }
    }
}
