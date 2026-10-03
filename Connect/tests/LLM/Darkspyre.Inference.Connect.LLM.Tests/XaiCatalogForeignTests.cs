using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiCatalogForeignTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedDefaultCacheDrainsThenReleasesItsActualForeignProducer(bool retire)
    {
        WeakReference[] roots = await HoldThenReleaseAsync(retire);
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
    private static async Task<WeakReference[]> HoldThenReleaseAsync(bool retire)
    {
        Created created = CreateForeign();
        Task? retirement = null;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            // MethodInfo is execution provenance, not an owned resource required to stay alive.
            for (int index = 0; index < 4; index++) Assert.True(created.Roots[index].IsAlive);
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
    private static Created CreateForeign()
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
            Type type = fixture.GetType(typeof(ForeignCatalogClient).FullName!, true)!;
            Assert.Same(fixture, type.Assembly);
            Assert.Contains(typeof(ILlmClient), type.GetInterfaces());
            Assert.Contains(typeof(ILlmModelCatalogClient), type.GetInterfaces());
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(ILlmClient).Assembly));
            var client = (ILlmClient)Activator.CreateInstance(type,
                (Func<int, CancellationToken, Task<IReadOnlyList<JsonElement>>>)state.ReadAsync, (Action<string>)state.Record)!;
            MethodInfo method = type.GetMethod(nameof(ForeignCatalogClient.ReadCatalogPartAsync))!;
            Assert.Equal(fixture.ManifestModule.ModuleVersionId, method.Module.ModuleVersionId);
            using var caller = new CancellationTokenSource();
            Task wait = Get(cache, client, caller.Token);
            state.Entered.Task.WaitAsync(Bound).GetAwaiter().GetResult();
            caller.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => wait.GetAwaiter().GetResult());
            Assert.Equal(1, state.GenericReads);
            Assert.Equal(0, state.Disposes);
            returned = true;
            return new(cache, state, [new(generation), new(fixture), new(type), new(client), new(method)]);
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
}
