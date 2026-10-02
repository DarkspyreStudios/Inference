using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM.Vertex.Tests;

public sealed class VertexCacheLifetimeTests
{
    private const string Name = "projects/123456/locations/global/cachedContents/cache-1";
    private const string Model = "google/gemini-2.5-flash";
    private const string Receipt = "{\"name\":\"" + Name + "\",\"model\":\"models/gemini-2.5-flash\",\"expireTime\":\"2050-01-01T00:00:00Z\"}";

    [Theory]
    [InlineData("create")]
    [InlineData("get")]
    [InlineData("list")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task AllCrudResponseCleanupFailuresRetainActualOwnerAndSharedRetirementFailure(string method)
    {
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var auth = new Authentication(); var source = new Source();
        var facade = new LlmProviderService(source, [new VertexLlmClient(http, auth)]);
        try
        {
            var caches = await facade.BindContextCacheAsync("connection");
            LlmExplicitCacheReference reference = new(Name, Model, "imported");
            if (method is "update" or "delete") reference = (await caches.GetAsync(reference)).Reference;
            var content = new RefusingContent(method == "list" ? "{\"cachedContents\":[" + Receipt + "]}" : Receipt);
            handler.Next = new(HttpStatusCode.OK) { Content = content };
            Exception operation = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                switch (method)
                {
                    case "create": _ = await caches.CreateAsync(new() { ModelId = Model, Correlation = new("operation", "display"),
                        TimeToLive = TimeSpan.FromHours(1), Messages = [new("user", "prefix")] }); break;
                    case "get": _ = await caches.GetAsync(reference); break;
                    case "list": _ = await caches.ListAsync(new(10)); break;
                    case "update": _ = await caches.UpdateExpiryAsync(reference, DateTimeOffset.UtcNow.AddHours(1)); break;
                    default: _ = await caches.DeleteAsync(reference); break;
                }
            });
            var cleanup = FindCleanup(operation);
            Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
            Assert.DoesNotContain("private-token-or-prefix", operation.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, content.CleanupAttempts);
            Exception first = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => facade.DisposeAsync().AsTask());
            Exception second = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => facade.DisposeAsync().AsTask());
            Assert.Same(cleanup, first); Assert.Same(first, second); Assert.Equal(1, content.CleanupAttempts);
            Assert.Equal(0, source.DisposeCalls); Assert.Equal(0, auth.DisposeCalls);
        }
        finally { try { await facade.DisposeAsync(); } catch (InferenceResourceOperationException) { } }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IgnoringOpeningDrainsAndLateResponseIsNeverPublished(bool authOpening)
    {
        var entered = Signal(); var release = Signal();
        using var handler = new Handler(); using var http = new HttpClient(handler);
        var auth = new Authentication();
        if (authOpening) auth.Wait = async () => { entered.TrySetResult(); await release.Task; };
        else handler.Wait = async () => { entered.TrySetResult(); await release.Task; };
        var facade = new LlmProviderService(new Source(), [new VertexLlmClient(http, auth)]);
        Task<LlmContextCache>? get = null; Task? retirement = null;
        try
        {
            var caches = await facade.BindContextCacheAsync("connection");
            get = caches.GetAsync(new(Name, Model, "imported"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = facade.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted); Assert.False(get.IsCompleted);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => get);
            await retirement;
            Assert.Equal(authOpening ? 0 : 1, handler.Sends);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => caches.GetAsync(new(Name, Model, "imported")));
        }
        finally
        {
            retirement ??= facade.DisposeAsync().AsTask(); release.TrySetResult();
            try { if (get is not null) await Observe(get); } finally { await retirement; }
        }
    }

    [Fact]
    public async Task SynchronousOwnerFenceRejectsLateResponseWhileLinkedCancellationIsHeld()
    {
        var entered = Signal(); var release = Signal(); var callbackEntered = Signal();
        using var callbackRelease = new ManualResetEventSlim();
        using var handler = new Handler { Wait = async () => { entered.TrySetResult(); await release.Task; } };
        using var http = new HttpClient(handler);
        var auth = new Authentication();
        var facade = new LlmProviderService(new Source(), [new VertexLlmClient(http, auth)]);
        Task<LlmContextCache>? get = null; Task? retirement = null; CancellationTokenRegistration registration = default;
        try
        {
            var caches = await facade.BindContextCacheAsync("connection");
            get = caches.GetAsync(new(Name, Model, "imported"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            registration = auth.Token.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Wait(); });
            retirement = facade.DisposeAsync().AsTask();
            // The owner fence is authoritative even if cancellation callbacks cannot complete yet.
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => get);
            Assert.False(retirement.IsCompleted);
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            retirement ??= facade.DisposeAsync().AsTask(); release.TrySetResult(); callbackRelease.Set();
            try { if (get is not null) await Observe(get); } finally { await retirement; registration.Dispose(); }
        }
    }

    [Fact]
    public async Task IgnoringBodyReadDrainsAndRefusesLateParsedReceipt()
    {
        var entered = Signal(); var release = Signal();
        var content = new HeldContent(entered, release);
        using var handler = new Handler { Next = new(HttpStatusCode.OK) { Content = content } };
        using var http = new HttpClient(handler);
        var facade = new LlmProviderService(new Source(), [new VertexLlmClient(http, new Authentication())]);
        Task<LlmContextCache>? get = null; Task? retirement = null;
        try
        {
            var caches = await facade.BindContextCacheAsync("connection");
            get = caches.GetAsync(new(Name, Model, "imported"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = facade.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted); Assert.False(get.IsCompleted);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => get);
            await retirement; Assert.Equal(1, content.CleanupAttempts);
        }
        finally
        {
            retirement ??= facade.DisposeAsync().AsTask(); release.TrySetResult();
            try { if (get is not null) await Observe(get); } finally { await retirement; }
        }
    }

    [Fact]
    public void RetainedProviderDefinitionUsesSharedImmutableContainers()
    {
        var definition = VertexProviderDefinition.Value;
        Assert.Equal("System.Private.CoreLib", definition.SetupSteps.GetType().Assembly.GetName().Name);
        Assert.Equal(3, definition.SetupSteps.Count);
        foreach (var step in definition.SetupSteps)
        {
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(step.GetType().Assembly));
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(step.Sections.GetType().Assembly));
            Assert.Empty(step.Sections);
        }
    }

    [Fact]
    public void RetainedDefaultFacadeReleasesActualForeignVertexAndBorrowedCallbacksAfterDrain()
    {
        var (facade, roots) = ExerciseForeign();
        try
        {
            for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
            { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.All(roots, root => Assert.False(root.IsAlive)); GC.KeepAlive(facade);
        }
        finally { facade.Dispose(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LlmProviderService Facade, WeakReference[] Roots) ExerciseForeign()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(VertexCacheLifetimeTests).Assembly.Location)!);
        try
        {
            Assembly vertex = generation.LoadFromAssemblyPath(typeof(VertexLlmClient).Assembly.Location);
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(VertexCacheLifetimeTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(vertex)); Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Assert.Equal(typeof(VertexLlmClient).Assembly.ManifestModule.ModuleVersionId, vertex.ManifestModule.ModuleVersionId);
            Assert.Equal(typeof(VertexCacheLifetimeTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Type type = fixture.GetType(typeof(VertexCacheLifetimeTests).FullName!, true)!;
            MethodInfo factory = type.GetMethod(nameof(CreateBorrowedGraph))!;
            Assert.Same(fixture, factory.Module.Assembly);
            var graph = (object[])factory.Invoke(null, null)!;
            var source = (ILlmConnectionSource)graph[0]; var client = (ILlmClient)graph[1]; var auth = graph[2]; var http = (HttpClient)graph[3];
            Assert.Same(fixture, source.GetType().Assembly); Assert.Same(fixture, auth.GetType().Assembly); Assert.Same(vertex, client.GetType().Assembly);
            Assert.Contains(typeof(ILlmClient), client.GetType().GetInterfaces());
            MethodInfo callback = source.GetType().GetMethod(nameof(ILlmConnectionSource.GetConnectionAsync))!;
            Assert.Same(fixture, callback.Module.Assembly);
            var facade = new LlmProviderService(source, [client]);
            var cache = facade.BindContextCacheAsync("connection").GetAwaiter().GetResult();
            Assert.Equal(Name, cache.GetAsync(new(Name, Model, "imported")).GetAwaiter().GetResult().Reference.ResourceName);
            facade.Dispose();
            Assert.Equal(0, source.GetType().GetProperty(nameof(Source.DisposeCalls))!.GetValue(source));
            Assert.Equal(0, auth.GetType().GetProperty(nameof(Authentication.DisposeCalls))!.GetValue(auth));
            Assert.Equal(1, auth.GetType().GetProperty(nameof(Authentication.Calls))!.GetValue(auth));
            http.Dispose();
            return (facade, [new(generation), new(vertex), new(fixture), new(type), new(factory), new(callback),
                new(source), new(client), new(auth), new(client.GetType()), new(http)]);
        }
        finally { generation.Unload(); }
    }

    public static object[] CreateBorrowedGraph()
    {
        var auth = new Authentication(); var http = new HttpClient(new Handler());
        return [new Source(), new VertexLlmClient(http, auth), auth, http];
    }

    private static InferenceResourceOperationException FindCleanup(Exception error) => error switch
    {
        InferenceResourceOperationException resource when resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup => resource,
        AggregateException aggregate => aggregate.InnerExceptions.Select(FindCleanupOrNull).First(result => result is not null)!,
        _ => FindCleanup(error.InnerException ?? throw new InvalidOperationException("No cleanup carrier."))
    };
    private static InferenceResourceOperationException? FindCleanupOrNull(Exception error)
    { try { return FindCleanup(error); } catch (InvalidOperationException) { return null; } }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Observe(Task operation) { try { await operation; } catch (OperationCanceledException) { } }
    private sealed class Source : ILlmConnectionSource, IDisposable
    {
        public int DisposeCalls { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(new()
        { Transport = LlmTransport.GoogleVertexAi, Version = "1", Endpoint = "https://aiplatform.googleapis.com",
            Vertex = new() { ProjectId = "project-id", Location = "global" }, CapabilityOverrides = new() { SupportsModelListing = false } });
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public void Dispose() => DisposeCalls++;
    }
    private sealed class Authentication : IVertexAuthentication, IDisposable
    {
        public int DisposeCalls { get; private set; }
        public int Calls { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal Func<Task>? Wait { get; set; }
        public async Task<VertexAccess> ResolveAsync(LlmConnection connection, CancellationToken cancellationToken)
        { Calls++; Token = cancellationToken; if (Wait is { } wait) await wait(); return new("controlled", "source"); }
        public void Dispose() => DisposeCalls++;
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal Func<Task>? Wait { get; set; }
        internal int Sends { get; private set; }
        internal HttpResponseMessage? Next { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Sends++; if (Wait is { } wait) await wait(); var result = Next; Next = null; return result ?? new(HttpStatusCode.OK) { Content = new StringContent(Receipt) }; }
    }
    private sealed class RefusingContent(string body) : StringContent(body)
    {
        internal int CleanupAttempts { get; private set; }
        protected override void Dispose(bool disposing)
        { base.Dispose(disposing); if (disposing) { CleanupAttempts++; throw new InvalidOperationException("private-token-or-prefix"); } }
    }
    private sealed class HeldContent(TaskCompletionSource entered, TaskCompletionSource release) : HttpContent
    {
        internal int CleanupAttempts { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { entered.TrySetResult(); await release.Task; await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Receipt)); }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) CleanupAttempts++; }
    }
    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name is "Darkspyre.Inference.Connect.LLM.Vertex" or "Darkspyre.Inference.Connect.LLM.Vertex.Tests"
            ? LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null;
    }
}
