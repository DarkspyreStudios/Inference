using System.Reflection;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiCatalogForeignTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RetainedDefaultCacheDrainsThenReleasesItsActualForeignProducer(bool retire, bool realHttp)
    {
        WeakReference[] roots = await HoldThenReleaseAsync(retire, realHttp);
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [Fact]
    public async Task RetainedDefaultCacheKeepsItsActualRefusedForeignResponseAfterBorrowersCollect()
    {
        WeakReference[] roots = await HoldRefusedResponseAsync();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> HoldRefusedResponseAsync()
    {
        Failed created = CreateRefusedResponse();
        try
        {
            for (int attempt = 0; created.Borrowers.Any(root => root.IsAlive) && attempt < 20; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(10);
            }
            Assert.All(created.Borrowers, root => Assert.False(root.IsAlive));
            // Only the retained Default owner remains: the constructing HTTP graph has collected.
            for (int index = 0; index < created.Roots.Length - 1; index++) Assert.True(created.Roots[index].IsAlive);
            var first = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => created.Cache.DisposeAsync().AsTask());
            Assert.Same(first, await Assert.ThrowsAsync<InferenceResourceOperationException>(() => created.Cache.DisposeAsync().AsTask()));
            Assert.DoesNotContain("foreign-cleanup-private", first.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, created.CleanupCalls());
            return created.Roots;
        }
        finally
        {
            try { await created.Cache.DisposeAsync(); }
            catch (InferenceResourceOperationException) { }
            finally { GC.KeepAlive(created.Cache); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Failed CreateRefusedResponse()
    {
        var generation = new FixtureGeneration();
        var cache = new LlmModelListCache();
        int cleanups = 0;
        bool returned = false;
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(XaiCatalogForeignTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Assert.Equal(typeof(XaiCatalogForeignTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Type type = fixture.GetType(typeof(ForeignRefusingContent).FullName!, true)!;
            Assert.Same(fixture, type.Assembly);
            var content = (HttpContent)Activator.CreateInstance(type, (Action)(() => Interlocked.Increment(ref cleanups)))!;
            MethodInfo method = type.GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal(fixture.ManifestModule.ModuleVersionId, method.Module.ModuleVersionId);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            var handler = new FixedResponseHandler(response);
            var http = new HttpClient(handler);
            var client = new XaiResponsesLlmClient(http);
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(client.GetType().Assembly));
            var failure = Assert.Throws<InferenceResourceOperationException>(() => Get(cache, client).WaitAsync(Bound).GetAwaiter().GetResult());
            Exception? current = failure;
            while (current is not null && current.GetType().GetProperty("Original", BindingFlags.Instance | BindingFlags.NonPublic) is null)
                current = current.InnerException;
            Assert.NotNull(current);
            object original = current.GetType().GetProperty("Original", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current)!;
            Assert.Same(type.GetProperty(nameof(ForeignRefusingContent.Failure))!.GetValue(content), original);
            Assert.Equal(1, cleanups);
            returned = true;
            return new(cache, [new(generation), new(fixture), new(type), new(content), new(response), new(method)],
                [new(client), new(http), new(handler)], () => cleanups);
        }
        finally
        {
            generation.Unload();
            if (!returned)
                try { cache.DisposeAsync().AsTask().WaitAsync(Bound).GetAwaiter().GetResult(); }
                catch (InferenceResourceOperationException) { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> HoldThenReleaseAsync(bool retire, bool realHttp)
    {
        Created created = CreateForeign(realHttp);
        Task? retirement = null;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            // MethodInfo is execution provenance, not an owned resource required to stay alive.
            for (int index = 0; index < created.Roots.Length - 1; index++) Assert.True(created.Roots[index].IsAlive);
            Assert.Equal(1, created.State.GenericReads);
            Assert.Equal(0, created.State.FamilyReads);
            if (retire)
            {
                retirement = created.Cache.DisposeAsync().AsTask();
                Assert.False(retirement.IsCompleted);
                Assert.Same(retirement, created.Cache.DisposeAsync().AsTask());
                await Assert.ThrowsAsync<ObjectDisposedException>(() => Get(created.Cache, new ForeignCatalogClient(created.State.ReadAsync, created.State.Record)));
            }
            created.State.Release.TrySetResult();
            if (retire) await retirement!.WaitAsync(Bound);
            else
            {
                IReadOnlyList<LlmModel> models = await Get(created.Cache, new ForeignCatalogClient(created.State.ReadAsync, created.State.Record)).WaitAsync(Bound);
                Assert.Equal("grok-4.7", Assert.Single(models).Id);
                Assert.Same(models, await Get(created.Cache, new ForeignCatalogClient(created.State.ReadAsync, created.State.Record)));
                Assert.Equal(1, created.State.FamilyReads);
            }
            Assert.Equal(0, created.State.Disposes);
            await CollectWhileCacheIsHeldAsync(created);
            return created.Roots;
        }
        finally
        {
            retirement ??= created.Cache.DisposeAsync().AsTask();
            created.State.Release.TrySetResult();
            try { await retirement.WaitAsync(Bound); }
            finally { GC.KeepAlive(created.Cache); }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CollectWhileCacheIsHeldAsync(Created created)
    {
        for (int attempt = 0; created.Roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }
        Assert.All(created.Roots, root => Assert.False(root.IsAlive));
        GC.KeepAlive(created.Cache);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Created CreateForeign(bool realHttp)
    {
        var generation = new FixtureGeneration();
        var state = new State();
        var cache = new LlmModelListCache();
        bool returned = false;
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(XaiCatalogForeignTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Assert.Equal(typeof(XaiCatalogForeignTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Type type = fixture.GetType((realHttp ? typeof(ForeignHttpHandler) : typeof(ForeignCatalogClient)).FullName!, true)!;
            Assert.Same(fixture, type.Assembly);
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(ILlmClient).Assembly));
            ILlmClient client;
            MethodInfo method;
            object? handler = null;
            HttpClient? http = null;
            if (realHttp)
            {
                Assert.True(type.IsSubclassOf(typeof(HttpMessageHandler)));
                handler = Activator.CreateInstance(type,
                    (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)state.SendAsync, (Action<string>)state.Record)!;
                http = new((HttpMessageHandler)handler);
                client = new XaiResponsesLlmClient(http);
                Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(client.GetType().Assembly));
                Assert.Equal(typeof(XaiResponsesLlmClient).Assembly.ManifestModule.ModuleVersionId, client.GetType().Assembly.ManifestModule.ModuleVersionId);
                method = type.GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            }
            else
            {
                Assert.Contains(typeof(ILlmClient), type.GetInterfaces());
                Assert.Contains(typeof(ILlmModelCatalogClient), type.GetInterfaces());
                client = (ILlmClient)Activator.CreateInstance(type,
                    (Func<int, CancellationToken, Task<IReadOnlyList<JsonElement>>>)state.ReadAsync, (Action<string>)state.Record)!;
                method = type.GetMethod(nameof(ForeignCatalogClient.ReadCatalogPartAsync))!;
            }
            Assert.Equal(fixture.ManifestModule.ModuleVersionId, method.Module.ModuleVersionId);
            using var caller = new CancellationTokenSource();
            Task wait = Get(cache, client, caller.Token);
            state.Entered.Task.WaitAsync(Bound).GetAwaiter().GetResult();
            caller.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => wait.GetAwaiter().GetResult());
            Assert.Equal(1, state.GenericReads);
            Assert.Equal(0, state.Disposes);
            returned = true;
            return new(cache, state, realHttp
                ? [new(generation), new(fixture), new(type), new(handler), new(http), new(client), new(method)]
                : [new(generation), new(fixture), new(type), new(client), new(method)]);
        }
        finally
        {
            generation.Unload();
            if (!returned)
            {
                Task retirement = cache.DisposeAsync().AsTask();
                state.Release.TrySetResult();
                retirement.WaitAsync(Bound).GetAwaiter().GetResult();
            }
        }
    }

    private static Task<IReadOnlyList<LlmModel>> Get(LlmModelListCache cache, ILlmClient client, CancellationToken token = default) =>
        cache.GetAsync("selected", new("v1", null), client, Connection, ModelCatalogFamily.Chat, token);

    private static LlmConnection Connection => new()
    {
        Version = "v1",
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Xai = new(),
        Credential = new LlmCredential { OAuth = new() { AccessToken = "controlled-token" } }.Serialize()
    };

    private sealed record Created(LlmModelListCache Cache, State State, WeakReference[] Roots);
    private sealed record Failed(LlmModelListCache Cache, WeakReference[] Roots, WeakReference[] Borrowers, Func<int> CleanupCalls);

    private sealed class FixedResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class State
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int GenericReads;
        internal int FamilyReads;
        internal int Disposes;
        internal async Task<IReadOnlyList<JsonElement>> ReadAsync(int part, CancellationToken token)
        {
            if (part == (int)ModelCatalogPart.Generic)
            {
                Interlocked.Increment(ref GenericReads);
                Entered.TrySetResult();
                await Release.Task; // The controlled borrowed producer deliberately ignores cancellation.
            }
            else Interlocked.Increment(ref FamilyReads);
            using JsonDocument document = JsonDocument.Parse("[{\"id\":\"grok-4.7\"}]");
            return Array.AsReadOnly(document.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray());
        }
        internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            bool generic = request.RequestUri!.AbsolutePath == "/v1/models";
            Assert.True(generic || request.RequestUri.AbsolutePath == "/v1/language-models");
            IReadOnlyList<JsonElement> rows = await ReadAsync((int)(generic ? ModelCatalogPart.Generic : ModelCatalogPart.Chat), token);
            return new(HttpStatusCode.OK)
            { Content = new StringContent("{\"" + (generic ? "data" : "models") + "\":" + JsonSerializer.Serialize(rows) + "}") };
        }
        internal void Record(string value) { if (value == "dispose") Interlocked.Increment(ref Disposes); }
    }

    private sealed class FixtureGeneration() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => Default.Assemblies.FirstOrDefault(assembly =>
            AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName()));
    }

    public sealed class ForeignCatalogClient(Func<int, CancellationToken, Task<IReadOnlyList<JsonElement>>> read,
        Action<string> record) : ILlmClient, ILlmModelCatalogClient, IDisposable
    {
        public LlmTransport Transport => LlmTransport.XaiResponses;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.GrokSubscription;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new() { SupportsModelListing = true };
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        Task<IReadOnlyList<JsonElement>> ILlmModelCatalogClient.ReadCatalogPartAsync(LlmConnection snapshot,
            ModelCatalogPart part, Action ownerFence, CancellationToken cancellationToken) => ReadCatalogPartAsync((int)part, ownerFence, cancellationToken);
        public async Task<IReadOnlyList<JsonElement>> ReadCatalogPartAsync(int part, Action ownerFence, CancellationToken cancellationToken)
        {
            IReadOnlyList<JsonElement> rows = await read(part, cancellationToken);
            ownerFence();
            return rows;
        }
        IReadOnlyList<LlmModel> ILlmModelCatalogClient.ProjectCatalog(LlmConnection snapshot, ModelCatalogFamily family,
            IReadOnlyList<JsonElement> generic, IReadOnlyList<JsonElement> specific) => XaiCatalog.Project(generic, specific);
        public void Dispose() => record("dispose");
    }

    public sealed class ForeignHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Action<string> record) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            await send(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) record("dispose"); base.Dispose(disposing); }
    }

    public sealed class ForeignRefusingContent(Action cleanup) : StringContent("{\"data\":[]}")
    {
        public Exception Failure { get; } = new InvalidOperationException("foreign-cleanup-private");
        public bool AllowCleanup { get; set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { cleanup(); if (!AllowCleanup) throw Failure; }
            base.Dispose(disposing);
        }
    }
}
