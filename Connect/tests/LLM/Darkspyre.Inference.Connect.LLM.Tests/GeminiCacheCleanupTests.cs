using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GeminiCacheCleanupTests
{
    private const string Receipt = "{\"name\":\"cachedContents/native\",\"model\":\"models/gemini-3-flash-preview\",\"expireTime\":\"2099-01-01T00:00:00Z\"}";

    [Theory]
    [InlineData("create")]
    [InlineData("get")]
    [InlineData("list")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task ActualResponseCleanupFailureIsRetainedBySharedFacadeRetirement(string operation)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var source = new Source(); var facade = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        try
        {
            ILlmContextCacheClient cache = await facade.BindContextCacheAsync("selected");
            var receipt = await cache.GetAsync(new("cachedContents/native", "gemini-3-flash-preview", "imported"));
            var content = new RefusingContent(operation == "list" ? "{\"cachedContents\":[" + Receipt + "]}" : Receipt);
            handler.Next = new(HttpStatusCode.OK) { Content = content };
            Exception? failure = await Record.ExceptionAsync(() => operation switch
            {
                "create" => cache.CreateAsync(new() { ModelId = receipt.Reference.ModelId, Correlation = new("operation", "display"), TimeToLive = TimeSpan.FromMinutes(1) }),
                "list" => cache.ListAsync(new(1)),
                "update" => cache.UpdateExpiryAsync(receipt.Reference, DateTimeOffset.UtcNow.AddHours(1)),
                "delete" => cache.DeleteAsync(receipt.Reference),
                _ => cache.GetAsync(receipt.Reference)
            });
            Assert.NotNull(failure);
            Assert.DoesNotContain("private-prefix-or-token", failure.ToString(), StringComparison.Ordinal);
            Task first = facade.DisposeAsync().AsTask(); Task second = facade.DisposeAsync().AsTask();
            Exception? firstFailure = await Record.ExceptionAsync(() => first);
            Exception? secondFailure = await Record.ExceptionAsync(() => second);
            Assert.NotNull(firstFailure); Assert.Same(firstFailure, secondFailure);
            Assert.Equal(1, content.CleanupAttempts);
            Assert.False(source.Disposed);
        }
        finally { try { await facade.DisposeAsync(); } catch (Exception) { } }
    }

    [Fact]
    public async Task OrdinaryProviderFailureDoesNotPoisonRetirement()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var source = new Source(); var facade = new LlmProviderService(source, [new GeminiApiLlmClient(http)]);
        try
        {
            var cache = await facade.BindContextCacheAsync("selected");
            handler.Next = new(HttpStatusCode.Forbidden) { Content = new StringContent("private-prefix-or-token") };
            _ = await Assert.ThrowsAsync<LlmProviderException>(() => cache.GetAsync(new("cachedContents/native", "gemini-3-flash-preview", "imported")));
            await facade.DisposeAsync(); await facade.DisposeAsync();
            Assert.False(source.Disposed);
        }
        finally { await facade.DisposeAsync(); }
    }

    [Fact]
    public void RetainedDefaultFacadeOwnsActualSeparatelyForeignRefusedResponse()
    {
        (LlmProviderService facade, WeakReference[] roots) = ExerciseForeignRefusal();
        try
        {
            for (int attempt = 0; attempt < 3; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.All(roots, root => Assert.True(root.IsAlive));
            GC.KeepAlive(facade);
        }
        finally { try { facade.Dispose(); } catch (Exception) { } }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LlmProviderService Facade, WeakReference[] Roots) ExerciseForeignRefusal()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(GeminiCacheCleanupTests).Assembly.Location)!);
        LlmProviderService? facade = null;
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(GeminiCacheCleanupTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Assert.Equal(typeof(GeminiCacheCleanupTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Type type = fixture.GetType(typeof(GeminiCacheCleanupTests).FullName!, true)!;
            MethodInfo factory = type.GetMethod(nameof(CreateRefusingResponse))!;
            var response = (HttpResponseMessage)factory.Invoke(null, null)!;
            object content = response.Content;
            Assert.Same(fixture, content.GetType().Assembly);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(content.GetType().Assembly));
            using var handler = new Handler { Next = response }; using var http = new HttpClient(handler);
            facade = new(new Source(), [new GeminiApiLlmClient(http)]);
            var cache = facade.BindContextCacheAsync("selected").GetAwaiter().GetResult();
            try { _ = cache.GetAsync(new("cachedContents/native", "gemini-3-flash-preview", "imported")).GetAwaiter().GetResult(); }
            catch (Exception) { }
            Assert.Equal(1, content.GetType().GetProperty(nameof(RefusingContent.CleanupAttempts))!.GetValue(content));
            Assert.Null(handler.Next);
            try { facade.Dispose(); } catch (Exception) { }
            return (facade, [new(generation), new(fixture), new(content.GetType()), new(content), new(response)]);
        }
        finally { generation.Unload(); }
    }

    public static HttpResponseMessage CreateRefusingResponse() => new(HttpStatusCode.OK) { Content = new RefusingContent(Receipt) };

    private sealed class RefusingContent(string body) : StringContent(body)
    {
        public int CleanupAttempts { get; private set; }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            CleanupAttempts++;
            throw new InvalidOperationException("private-prefix-or-token");
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal HttpResponseMessage? Next { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage? response = Next; Next = null;
            return Task.FromResult(response ?? new(HttpStatusCode.OK) { Content = new StringContent(Receipt) });
        }
    }

    private sealed class Source : ILlmConnectionSource, IDisposable
    {
        internal bool Disposed { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new() { Version = "version", Transport = LlmTransport.GoogleGeminiApi,
                Endpoint = GeminiApiLlmClient.DefaultEndpoint, Credential = new LlmCredential { ApiKey = "private" }.Serialize() });
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected refresh.");
        public void Dispose() => Disposed = true;
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name == "Darkspyre.Inference.Connect.LLM.Tests"
            ? LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null;
    }
}
