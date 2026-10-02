using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class GoogleCacheCrudRequestTests
{
    private static readonly bool[] BooleanValues = [false, true];
    private static readonly string[] MethodNames = ["create", "get", "list", "update", "delete"];
    private static readonly string[] Phases = ["parameter", "fence", "consumption", "cancellation"];
    public static TheoryData<bool, string, bool> Methods
    {
        get
        {
            var data = new TheoryData<bool, string, bool>();
            foreach (bool vertex in BooleanValues) foreach (string method in MethodNames) foreach (bool response in BooleanValues)
                data.Add(vertex, method, response);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Methods))]
    public async Task RoutedCrudRequestCleanupPreservesReceiptsAndSharedRetirement(bool vertex, string method, bool refuseResponse)
    {
        using var handler = new Handler(vertex); using var http = new HttpClient(handler);
        var source = new Source(vertex); var auth = new Authentication();
        var facade = new LlmProviderService(source, [vertex ? new VertexLlmClient(http, auth) : new GeminiApiLlmClient(http)]);
        try
        {
            ILlmContextCacheClient caches = await facade.BindContextCacheAsync("connection");
            var reference = (await caches.GetAsync(new(Name(vertex), Model(vertex), "import"))).Reference;
            handler.RefuseRequest = true; handler.RefuseResponse = refuseResponse;
            Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => Invoke(caches, method, reference));
            Exception[] cleanup = Errors(failure).Where(IsCleanup).ToArray();
            Assert.Equal(refuseResponse ? 2 : 1, cleanup.Length);
            Assert.Contains("request cleanup", cleanup[^1].InnerException!.Message, StringComparison.Ordinal);
            if (refuseResponse) Assert.Contains("response cleanup", cleanup[0].InnerException!.Message, StringComparison.Ordinal);
            if (method == "create") Assert.NotNull(Assert.IsType<LlmContextCacheCreateUncertainException>(failure).ObservedReceipt);
            if (method == "update") Assert.NotNull(Assert.IsType<LlmContextCacheMutationUncertainException>(failure).ObservedUpdate);
            if (method == "delete") Assert.False(Assert.IsType<LlmContextCacheMutationUncertainException>(failure).ObservedDelete!.AlreadyAbsent);
            Assert.Equal(1, handler.RequestContent!.Calls); Assert.Equal(1, handler.ResponseContent!.Calls);
            Assert.DoesNotContain("private-crud-secret", failure.ToString(), StringComparison.Ordinal);
            Exception retirement = await Assert.ThrowsAnyAsync<Exception>(() => facade.DisposeAsync().AsTask());
            Assert.Same(retirement, await Assert.ThrowsAnyAsync<Exception>(() => facade.DisposeAsync().AsTask()));
            Assert.Equal(cleanup, Errors(retirement).Where(IsCleanup).ToArray());
            Assert.Equal(1, handler.RequestContent.Calls); Assert.Equal(0, source.Disposals); Assert.Equal(0, auth.Disposals);
        }
        finally { try { await facade.DisposeAsync(); } catch (Exception error) when (Errors(error).Any(IsCleanup)) { } }
    }

    [Theory]
    [InlineData(false, "create")]
    [InlineData(false, "update")]
    [InlineData(true, "create")]
    [InlineData(true, "update")]
    public async Task RoutedPrimaryFailurePrecedesBothIndependentCleanupFailures(bool vertex, string method)
    {
        using var handler = new Handler(vertex); using var http = new HttpClient(handler);
        var facade = new LlmProviderService(new Source(vertex), [vertex ? new VertexLlmClient(http, new Authentication()) : new GeminiApiLlmClient(http)]);
        try
        {
            var caches = await facade.BindContextCacheAsync("connection");
            var reference = (await caches.GetAsync(new(Name(vertex), Model(vertex), "import"))).Reference;
            handler.RefuseRequest = true; handler.RefuseResponse = true; handler.Status = HttpStatusCode.ServiceUnavailable;
            Exception error = await Assert.ThrowsAnyAsync<Exception>(() => Invoke(caches, method, reference));
            var actual = Errors(error).OfType<LlmProviderException>().ToArray();
            Assert.Equal(3, actual.Length); Assert.Equal(HttpStatusCode.ServiceUnavailable, actual[0].StatusCode);
            Assert.Contains("rejected", actual[0].Message, StringComparison.Ordinal);
            Assert.Contains("response cleanup", actual[1].Message, StringComparison.Ordinal);
            Assert.Contains("request cleanup", actual[2].Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-crud-secret", error.ToString(), StringComparison.Ordinal);
        }
        finally { try { await facade.DisposeAsync(); } catch (Exception error) when (Errors(error).Any(IsCleanup)) { } }
    }

    public static TheoryData<string, string> AdmissionFailures
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (string method in MethodNames) foreach (string phase in Phases) data.Add(method, phase);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AdmissionFailures))]
    public async Task PredispatchFailuresCleanTheActualRequestWithoutMutationUncertainty(string method, string phase)
    {
        using var cancellation = new CancellationTokenSource();
        if (phase == "cancellation") cancellation.Cancel();
        CancellationToken token = cancellation.Token;
        using var handler = new Handler(false); using var borrowed = new HttpClient(handler);
        var services = new ServiceCollection(); services.AddLlmConnect();
        services.AddHttpClient("GeminiApiLlmClient").ConfigurePrimaryHttpMessageHandler(() => new Handler(false));
        await using var root = services.BuildServiceProvider(); var pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using var retired = pool.CreateClient("GeminiApiLlmClient");
        if (phase == "consumption") await pool.DisposeAsync();
        var request = new HttpRequestMessage(method switch { "create" => HttpMethod.Post, "update" => HttpMethod.Patch, "delete" => HttpMethod.Delete, _ => HttpMethod.Get }, "https://controlled.invalid/");
        var content = new Content(new StringContent("request"), true); request.Content = content;
        var primary = new InvalidOperationException("admission");
        Action<CancellationToken> fence = _ => { if (phase == "fence") throw primary; };
        Func<System.Text.Json.JsonElement, LlmContextCache> parse = _ => throw new InvalidOperationException();
        var reference = new LlmExplicitCacheReference(Name(false), Model(false), "scope");
        HttpClient http = phase == "consumption" ? retired : borrowed;
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => method switch
        {
            "create" => GoogleCacheHttp.CreateAsync(http, request, LlmTransport.GoogleGeminiApi, phase == "parameter" ? null! : new("operation", "display"), parse, fence, token),
            "get" => GoogleCacheHttp.GetAsync(http, request, LlmTransport.GoogleGeminiApi, phase == "parameter" ? null! : parse, fence, token),
            "list" => GoogleCacheHttp.ListAsync(http, request, LlmTransport.GoogleGeminiApi, phase == "parameter" ? null! : _ => new([], null), fence, token),
            "update" => GoogleCacheHttp.UpdateExpiryAsync(http, request, LlmTransport.GoogleGeminiApi, reference, phase == "parameter" ? null! : parse, fence, token),
            _ => GoogleCacheHttp.DeleteAsync(http, request, LlmTransport.GoogleGeminiApi, reference, phase == "parameter" ? null! : _ => true, fence, token)
        });
        Assert.Equal(1, content.Calls); Assert.Equal(0, handler.Sends); Assert.Single(Errors(error), IsCleanup);
        Assert.DoesNotContain(Errors(error), value => value is LlmContextCacheCreateUncertainException or LlmContextCacheMutationUncertainException);
        if (phase == "fence") Assert.Contains(Errors(error), value => ReferenceEquals(primary, value));
        if (phase == "cancellation") Assert.Equal(token, Assert.Single(Errors(error).OfType<OperationCanceledException>()).CancellationToken);
        Assert.DoesNotContain("private-crud-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizedNotFoundDeleteKeepsActualAcknowledgementWhenRequestCleanupRefuses(bool vertex)
    {
        using var handler = new Handler(vertex); using var http = new HttpClient(handler);
        var facade = new LlmProviderService(new Source(vertex), [vertex ? new VertexLlmClient(http, new Authentication()) : new GeminiApiLlmClient(http)]);
        try
        {
            var caches = await facade.BindContextCacheAsync("connection");
            var reference = (await caches.GetAsync(new(Name(vertex), Model(vertex), "import"))).Reference;
            Assert.False((await caches.DeleteAsync(reference)).AlreadyAbsent);
            handler.Status = HttpStatusCode.NotFound; handler.RefuseRequest = true;
            var error = await Assert.ThrowsAsync<LlmContextCacheMutationUncertainException>(() => caches.DeleteAsync(reference));
            Assert.True(error.ObservedDelete!.AlreadyAbsent); Assert.Single(Errors(error), IsCleanup);
            Assert.Equal(1, handler.RequestContent!.Calls); Assert.Equal(3, handler.Sends);
        }
        finally { try { await facade.DisposeAsync(); } catch (Exception error) when (Errors(error).Any(IsCleanup)) { } }
    }

    [Fact]
    public void ActualForeignCrudRequestOwnerIsRetainedByDefaultFacadeThenCollects()
    {
        WeakReference[] roots = ExerciseForeign();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(GoogleCacheCrudRequestTests).Assembly.Location)!);
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(GoogleCacheCrudRequestTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Assert.Equal(typeof(GoogleCacheCrudRequestTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Type type = fixture.GetType(typeof(GoogleCacheCrudRequestTests).FullName!, true)!;
            MethodInfo method = type.GetMethod(nameof(RefusingRequest))!; Assert.Same(fixture, method.Module.Assembly);
            using var handler = new Handler(true) { Wrap = method.CreateDelegate<Func<HttpContent, HttpContent>>() }; using var http = new HttpClient(handler);
            var facade = new LlmProviderService(new Source(true), [new VertexLlmClient(http, new Authentication())]);
            var caches = facade.BindContextCacheAsync("connection").GetAwaiter().GetResult();
            var operation = Assert.Throws<InferenceResourceOperationException>(() => caches.GetAsync(new(Name(true), Model(true), "import")).GetAwaiter().GetResult());
            Assert.Same(operation, Assert.Throws<InferenceResourceOperationException>(() => facade.Dispose()));
            Assert.Same(operation, Assert.Throws<InferenceResourceOperationException>(() => facade.Dispose()));
            var request = handler.Request!; var content = request.Content!;
            Assert.Same(fixture, content.GetType().Assembly); Assert.Same(generation, AssemblyLoadContext.GetLoadContext(content.GetType().Assembly));
            Assert.Equal(1, content.GetType().GetProperty(nameof(Content.Calls))!.GetValue(content));
            handler.Request = null; handler.RequestContent = null; handler.Wrap = null;
            var roots = new WeakReference[] { new(generation), new(fixture), new(type), new(method), new(request), new(content), new(content.GetType()) };
            for (int attempt = 0; attempt < 3; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.All(roots, root => Assert.True(root.IsAlive)); GC.KeepAlive(facade);
            return roots;
        }
        finally { generation.Unload(); }
    }

    public static HttpContent RefusingRequest(HttpContent original) => new Content(original, true);
    private static bool IsCleanup(Exception error) => error is InferenceResourceOperationException { Failure.Phase: InferenceResourceOperationPhase.Cleanup };
    private static IEnumerable<Exception> Errors(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate)
        { foreach (var child in aggregate.InnerExceptions) foreach (var value in Errors(child)) yield return value; }
        else if (error.InnerException is { } inner) foreach (var value in Errors(inner)) yield return value;
    }
    private static string Model(bool vertex) => vertex ? "google/gemini-2.5-flash" : "gemini-2.5-flash";
    private static string Name(bool vertex) => vertex ? "projects/123456/locations/global/cachedContents/cache-1" : "cachedContents/cache-1";
    private static async Task Invoke(ILlmContextCacheClient caches, string method, LlmExplicitCacheReference reference)
    {
        switch (method)
        {
            case "create": _ = await caches.CreateAsync(new() { ModelId = reference.ModelId, Correlation = new("operation", "display"), TimeToLive = TimeSpan.FromHours(1), Messages = [new("user", "prefix")] }); break;
            case "get": _ = await caches.GetAsync(reference); break;
            case "list": _ = await caches.ListAsync(new(10)); break;
            case "update": _ = await caches.UpdateExpiryAsync(reference, DateTimeOffset.Parse("2049-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)); break;
            default: _ = await caches.DeleteAsync(reference); break;
        }
    }
    private sealed class Source(bool vertex) : ILlmConnectionSource, IDisposable
    {
        internal int Disposals { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(new()
        {
            Version = "1",
            Transport = vertex ? LlmTransport.GoogleVertexAi : LlmTransport.GoogleGeminiApi,
            Endpoint = vertex ? "https://aiplatform.googleapis.com" : GeminiApiLlmClient.DefaultEndpoint,
            Credential = vertex ? null : new LlmCredential { ApiKey = "controlled" }.Serialize(),
            Vertex = vertex ? new() { ProjectId = "project-id", Location = "global" } : null,
            CapabilityOverrides = new() { SupportsModelListing = false }
        });
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void Dispose() => Disposals++;
    }
    private sealed class Authentication : IVertexAuthentication, IDisposable
    {
        internal int Disposals { get; private set; }
        public Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken) => Task.FromResult(new VertexAccess("controlled", "source"));
        public void Dispose() => Disposals++;
    }
    private sealed class Handler(bool vertex) : HttpMessageHandler
    {
        internal bool RefuseRequest { get; set; }
        internal bool RefuseResponse { get; set; }
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal int Sends { get; private set; }
        internal Content? RequestContent { get; set; }
        internal Content? ResponseContent { get; private set; }
        internal HttpRequestMessage? Request { get; set; }
        internal Func<HttpContent, HttpContent>? Wrap { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Sends++; Request = request;
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            var inner = request.Content ?? new StringContent("");
            request.Content = Wrap is { } wrap ? wrap(inner) : RequestContent = new(inner, RefuseRequest);
            var receipt = new JsonObject { ["name"] = Name(vertex), ["model"] = "models/gemini-2.5-flash", ["expireTime"] = "2050-01-01T00:00:00Z" };
            if (request.Method == HttpMethod.Patch) receipt["expireTime"] = JsonNode.Parse(body!)!["expireTime"]!.GetValue<string>();
            string response = request.Method == HttpMethod.Get && request.RequestUri!.Query.Length > 0
                ? new JsonObject { ["cachedContents"] = new JsonArray(receipt) }.ToJsonString() : receipt.ToJsonString();
            ResponseContent = new(new StringContent(response), RefuseResponse);
            return new(Status) { Content = ResponseContent };
        }
    }
    private sealed class Content(HttpContent inner, bool refuse) : HttpContent
    {
        public int Calls { get; private set; }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => inner.CopyToAsync(destination);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        { if (disposing) { Calls++; if (refuse) throw new InvalidOperationException("private-crud-secret"); inner.Dispose(); } base.Dispose(disposing); }
    }
    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    { protected override Assembly? Load(AssemblyName name) => name.Name == "Darkspyre.Inference.Connect.LLM.Vertex.Tests" ? LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null; }
}
