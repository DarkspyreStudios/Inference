using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GeminiCacheGenerationTests
{
    [Fact]
    public void ActualForeignLifecycleAndOwnedReceiptsRetireWithDefaultHttpRetained()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        WeakReference[] roots = ExerciseForeign(http);
        Assert.Equal(2, handler.CacheGets); Assert.Equal(1, handler.Counts); Assert.Equal(1, handler.Creates); Assert.Equal(1, handler.Uploads);
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
        GC.KeepAlive(http); GC.KeepAlive(handler);
    }

    [Fact]
    public void RetainedDefaultFacadeDoesNotRootSeparatelyForeignBorrowedSourceAfterRetirement()
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        (LlmProviderService facade, WeakReference[] roots) = ExerciseBorrowedSource(http);
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
        GC.KeepAlive(facade); GC.KeepAlive(http); GC.KeepAlive(handler);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LlmProviderService Facade, WeakReference[] Roots) ExerciseBorrowedSource(HttpClient http)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(GeminiCacheGenerationTests).Assembly.Location)!, shareContracts: true);
        var roots = new List<WeakReference> { new(generation) };
        LlmProviderService? facade = null;
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(GeminiCacheGenerationTests).Assembly.Location);
            Assert.Equal(typeof(GeminiCacheGenerationTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Type caller = fixture.GetType(typeof(GeminiCacheGenerationTests).FullName!, true)!;
            MethodInfo factory = caller.GetMethod(nameof(CreateBorrowedSource))!;
            var source = (ILlmConnectionSource)factory.Invoke(null, null)!;
            Assert.Same(fixture, source.GetType().Assembly);
            Assert.Equal(typeof(ILlmConnectionSource), source.GetType().GetInterfaces().Single(type => type == typeof(ILlmConnectionSource)));
            MethodInfo callback = source.GetType().GetMethod(nameof(ILlmConnectionSource.GetConnectionAsync))!;
            Assert.Same(fixture, callback.Module.Assembly);
            roots.AddRange([new(fixture), new(caller), new(factory), new(callback), new(source.GetType()), new(source)]);
            facade = new(source, [new GeminiApiLlmClient(http)]);
            ILlmContextCacheClient cache = facade.BindContextCacheAsync("selected").GetAwaiter().GetResult();
            _ = cache.GetAsync(new("cachedContents/native", "gemini-3-flash-preview", "imported")).GetAwaiter().GetResult();
            facade.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Assert.Equal(2, source.GetType().GetProperty(nameof(Source.Reads))!.GetValue(source));
            Assert.False((bool)source.GetType().GetProperty(nameof(Source.Disposed))!.GetValue(source)!);
            return (facade, roots.ToArray());
        }
        finally { facade?.Dispose(); generation.Unload(); }
    }

    public static object CreateBorrowedSource() => new Source();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign(HttpClient http)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(GeminiCacheGenerationTests).Assembly.Location)!);
        var roots = new List<WeakReference> { new(generation) };
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(GeminiCacheGenerationTests).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            foreach ((Assembly source, Assembly actual) in new[] { (typeof(GeminiCacheGenerationTests).Assembly, fixture),
                (typeof(ConnectLlmJson).Assembly, connect), (typeof(InferenceJson).Assembly, common) })
            {
                Assert.NotSame(source, actual); Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
                Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId); roots.Add(new(actual));
            }
            Type caller = fixture.GetType(typeof(GeminiCacheGenerationTests).FullName!, true)!;
            MethodInfo method = caller.GetMethod(nameof(ExerciseOwningGenerationAsync))!;
            roots.Add(new(caller)); roots.Add(new(method));
            var operation = (Task<WeakReference[]>)method.Invoke(null, [http])!;
            roots.Add(new(operation)); roots.AddRange(operation.GetAwaiter().GetResult());
        }
        finally { generation.Unload(); }
        return roots.ToArray();
    }

    public static async Task<WeakReference[]> ExerciseOwningGenerationAsync(HttpClient http)
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(GeminiCacheGenerationTests).Assembly)!;
        if (!generation.IsCollectible || AssemblyLoadContext.GetLoadContext(typeof(LlmProviderService).Assembly) != generation
            || AssemblyLoadContext.GetLoadContext(typeof(LlmExplicitCacheReference).Assembly) != generation)
            throw new InvalidOperationException("Fixture, actual facade and Common must execute in the private generation.");
        var source = new Source(); var adapter = new GeminiApiLlmClient(http); var owner = new LlmProviderService(source, [adapter]);
        var resources = new ControlledSessionResources();
        InferenceResourceReference named = await resources.RegisterAsync("source.txt", "text/plain", "abc");
        ILlmContextCacheClient? cache = null; LlmContextCache? receipt = null; LlmContextCache? restored = null;
        try
        {
            cache = await owner.BindContextCacheAsync("selected");
            receipt = await cache.GetAsync(new("cachedContents/native", "gemini-3-flash-preview", "imported"));
            var metadata = ConnectLlmJson.LlmContextCache;
            var referenceMetadata = InferenceJson.LlmExplicitCacheReference;
            var converter = metadata.Options.Converters.Single(value => value.CanConvert(typeof(LlmHostedToolKind)));
            var resolver = metadata.Options.TypeInfoResolver!;
            if (metadata.Type.Assembly != typeof(LlmProviderService).Assembly || referenceMetadata.Type.Assembly != typeof(LlmExplicitCacheReference).Assembly
                || converter.GetType().Assembly != typeof(LlmExplicitCacheReference).Assembly || resolver.GetType().Assembly != typeof(LlmProviderService).Assembly)
                throw new InvalidOperationException("Actual metadata, converter and resolver must use exact private owners.");
            restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(receipt, metadata), metadata)!;
            _ = await owner.CountInputTokensAsync("selected", new()
            {
                ModelId = receipt.Reference.ModelId,
                ExplicitCache = restored.Reference,
                Messages = [new("user", "suffix")]
            });
            receipt = await cache.GetAsync(restored.Reference);
            LlmContextCache created = await cache.CreateAsync(new()
            {
                ModelId = receipt.Reference.ModelId,
                Resources = resources,
                Correlation = new("operation", "display"),
                TimeToLive = TimeSpan.FromMinutes(1),
                Messages = [new("user", "prefix") { Attachments = [new LlmResourceAttachment(named)] }]
            });
            if (resources.SelectionReads != 0 || resources.DisposedReads != 1 || resources.DisposeCalls != 0
                || named.GetType().Assembly != typeof(InferenceJson).Assembly)
                throw new InvalidOperationException("Actual named Common graph must use the shared reader without selection callbacks or borrowed disposal.");
            await owner.DisposeAsync();
            try { _ = await cache.GetAsync(restored.Reference); throw new InvalidOperationException("Retired bound client must refuse."); }
            catch (ObjectDisposedException) { }
            if (source.Saves != 0 || source.Disposed) throw new InvalidOperationException("Borrowed source must not be saved or disposed.");
            return [new(owner), new(adapter), new(source), new(cache), new(receipt), new(restored), new(receipt.Reference), new(resources), new(named), new(created),
                new(metadata), new(referenceMetadata), new(converter), new(resolver), new(typeof(GeminiCacheBindings))];
        }
        finally { await owner.DisposeAsync(); }
    }

    private sealed class Source : ILlmConnectionSource, IDisposable
    {
        internal int Saves { get; private set; }
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default)
        {
            Reads++; return Task.FromResult<LlmConnection?>(new()
            {
                Version = "version",
                Transport = LlmTransport.GoogleGeminiApi,
                Endpoint = GeminiApiLlmClient.DefaultEndpoint,
                Credential = new LlmCredential { ApiKey = "private" }.Serialize()
            });
        }
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default)
        { Saves++; throw new InvalidOperationException("Unexpected save."); }
        public void Dispose() => Disposed = true;
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int CacheGets { get; private set; }
        internal int Counts { get; private set; }
        internal int Creates { get; private set; }
        internal int Uploads { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null) await request.Content.CopyToAsync(Stream.Null, cancellationToken);
            string payload;
            if (request.RequestUri!.AbsolutePath == "/v1beta/models") payload = "{\"models\":[]}";
            else if (request.RequestUri.AbsolutePath == "/upload/v1beta/files")
            { Uploads++; payload = "{\"file\":{\"name\":\"files/staged\",\"uri\":\"https://generativelanguage.googleapis.com/v1beta/files/staged\",\"mimeType\":\"text/plain\",\"state\":\"ACTIVE\",\"sizeBytes\":\"3\"}}"; }
            else if (request.RequestUri.AbsolutePath.EndsWith(":countTokens", StringComparison.Ordinal)) { Counts++; payload = "{\"totalTokens\":3}"; }
            else { if (request.Method == HttpMethod.Post) Creates++; else CacheGets++; payload = "{\"name\":\"cachedContents/native\",\"model\":\"models/gemini-3-flash-preview\",\"expireTime\":\"2099-01-01T00:00:00Z\"}"; }
            return new(HttpStatusCode.OK) { Content = new StringContent(payload) };
        }
    }

    private sealed class Generation(string directory, bool shareContracts = false) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name == "Darkspyre.Inference.Connect.LLM.Tests"
            || !shareContracts && name.Name is "Darkspyre.Inference.Common" or "Darkspyre.Inference.Connect.LLM"
            ? LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null;
    }
}
