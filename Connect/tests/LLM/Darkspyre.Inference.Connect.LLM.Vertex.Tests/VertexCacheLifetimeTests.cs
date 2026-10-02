using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

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
                    case "create":
                        _ = await caches.CreateAsync(new()
                        {
                            ModelId = Model,
                            Correlation = new("operation", "display"),
                            TimeToLive = TimeSpan.FromHours(1),
                            Messages = [new("user", "prefix")]
                        }); break;
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void RetainedDefaultFacadeReleasesActualForeignVertexAndBorrowedCallbacksAfterDrain(bool read, bool retain, bool neutral)
    {
        WeakReference[] roots = HoldThenReleaseForeign(read, retain, neutral);
        Collect(roots);
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] HoldThenReleaseForeign(bool read, bool retain, bool neutral)
    {
        var (facade, roots) = ExerciseForeign(read, retain, neutral);
        try
        {
            Collect(roots);
            Assert.All(roots, root => Assert.False(root.IsAlive));
            GC.KeepAlive(facade);
            return roots;
        }
        finally { facade?.Dispose(); }
    }

    private static void Collect(WeakReference[] roots)
    {
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
    }

    [Fact]
    public async Task RetainedProviderMetadataAloneReleasesActualForeignVertex()
    {
        var (metadata, roots) = ExerciseForeignMetadata();
        for (int attempt = 0; attempt < 20; attempt++)
        { await Task.Yield(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
        Assert.Equal(3, metadata.SetupSteps.Count); GC.KeepAlive(metadata);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LlmProviderDefinition Metadata, WeakReference[] Roots) ExerciseForeignMetadata()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(VertexCacheLifetimeTests).Assembly.Location)!);
        try
        {
            Assembly vertex = generation.LoadFromAssemblyPath(typeof(VertexLlmClient).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(vertex));
            Assert.Equal(typeof(VertexLlmClient).Assembly.ManifestModule.ModuleVersionId, vertex.ManifestModule.ModuleVersionId);
            Type type = vertex.GetType(typeof(VertexProviderDefinition).FullName!, true)!;
            MethodInfo getter = type.GetProperty(nameof(VertexProviderDefinition.Value))!.GetMethod!;
            Assert.Same(vertex, getter.Module.Assembly);
            var metadata = (LlmProviderDefinition)getter.Invoke(null, null)!;
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(metadata.GetType().Assembly));
            Assert.Equal("System.Private.CoreLib", metadata.SetupSteps.GetType().Assembly.GetName().Name);
            return (metadata, [new(generation), new(vertex), new(type), new(getter)]);
        }
        finally { generation.Unload(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LlmProviderService? Facade, WeakReference[] Roots) ExerciseForeign(bool read, bool retain, bool neutral)
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
            MethodInfo factory = type.GetMethod(neutral ? nameof(CreateNeutralBorrowedGraph) : nameof(CreateBorrowedGraph))!;
            Assert.Same(fixture, factory.Module.Assembly);
            var graph = (object[])factory.Invoke(null, null)!;
            var source = (ILlmConnectionSource)graph[0]; var client = (ILlmClient)graph[1]; var auth = graph[2]; var http = (HttpClient)graph[3];
            Assert.Same(fixture, source.GetType().Assembly); Assert.Same(fixture, auth.GetType().Assembly); Assert.Same(neutral ? fixture : vertex, client.GetType().Assembly);
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(client.ProviderDefinition.GetType().Assembly));
            Assert.Equal("System.Private.CoreLib", client.ProviderDefinition.SetupSteps.GetType().Assembly.GetName().Name);
            foreach (var step in client.ProviderDefinition.SetupSteps)
            {
                Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(step.GetType().Assembly));
                Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(step.Sections.GetType().Assembly));
            }
            Assert.Equal(1, client.GetType().GetInterfaces().Count(candidate => candidate == typeof(ILlmClient)));
            MethodInfo callback = source.GetType().GetMethod(nameof(ILlmConnectionSource.GetConnectionAsync))!;
            Assert.Same(fixture, callback.Module.Assembly);
            var facade = new LlmProviderService(source, [client]);
            if (read)
            {
                var cache = facade.BindContextCacheAsync("connection").GetAwaiter().GetResult();
                Assert.Equal(Name, cache.GetAsync(new(Name, Model, "imported")).GetAwaiter().GetResult().Reference.ResourceName);
            }
            facade.Dispose();
            Assert.Equal(0, source.GetType().GetProperty(nameof(Source.DisposeCalls))!.GetValue(source));
            Assert.Equal(0, auth.GetType().GetProperty(nameof(Authentication.DisposeCalls))!.GetValue(auth));
            Assert.Equal(read ? 1 : 0, auth.GetType().GetProperty(nameof(Authentication.Calls))!.GetValue(auth));
            http.Dispose();
            return (retain ? facade : null, [new(generation), new(vertex), new(fixture), new(type), new(factory), new(callback),
                new(source), new(client), new(auth), new(client.GetType()), new(http)]);
        }
        finally { generation.Unload(); }
    }

    public static object[] CreateBorrowedGraph()
    {
        var auth = new Authentication(); var http = new HttpClient(new Handler());
        return [new Source(), new VertexLlmClient(http, auth), auth, http];
    }

    public static object[] CreateNeutralBorrowedGraph()
    {
        var auth = new Authentication(); var http = new HttpClient(new Handler());
        return [new Source(), new NeutralClient(), auth, http];
    }

    private sealed class NeutralClient : ILlmClient, ILlmConnectionPreparation, ILlmContextEstimator, ILlmProviderTokenCounter, ILlmContextCacheAdapter
    {
        public LlmTransport Transport => LlmTransport.GoogleVertexAi;
        public LlmProviderDefinition ProviderDefinition { get; } = new()
        {
            Id = "neutral",
            DisplayName = "Neutral",
            Description = "Controlled interface caller",
            Transport = LlmTransport.GoogleVertexAi,
            DefaultEndpoint = "https://aiplatform.googleapis.com",
            SetupSteps = Array.AsReadOnly<Common.Setup.SetupStep>([])
        };
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => throw new NotSupportedException();
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LlmConnection> PrepareAsync(LlmConnection connection, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request) => throw new NotSupportedException();
        public string? EstimateDiagnostic => null;
        public Task<LlmProviderInputTokenCount> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void ValidateCacheConnection(LlmConnection connection) => throw new NotSupportedException();
        public void ValidateCacheReference(LlmConnection connection, LlmExplicitCacheReference reference) => throw new NotSupportedException();
        public void ValidateCacheCreate(LlmConnection connection, LlmContextCacheCreate request) => throw new NotSupportedException();
        public void ValidateCachedSuffix(LlmChatRequest request) => throw new NotSupportedException();
        public Task<ILlmContextCacheOperation> PrepareCacheOperationAsync(LlmConnection connection, Action<CancellationToken> fence, CancellationToken token) => throw new NotSupportedException();
    }

    [Fact]
    public void ActualForeignRefusedResponseRemainsOwnedUntilFacadeIsReleased()
    {
        WeakReference[] roots = ExerciseForeignRefusedResponse();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeignRefusedResponse()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(VertexCacheLifetimeTests).Assembly.Location)!);
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(VertexCacheLifetimeTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Assert.Equal(typeof(VertexCacheLifetimeTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Type type = fixture.GetType(typeof(VertexCacheLifetimeTests).FullName!, true)!;
            MethodInfo factory = type.GetMethod(nameof(CreateRefusedResponse))!;
            Assert.Same(fixture, factory.Module.Assembly);
            var response = (HttpResponseMessage)factory.Invoke(null, null)!;
            object content = response.Content;
            Assert.Same(fixture, content.GetType().Assembly);
            using var handler = new Handler { Next = response }; using var http = new HttpClient(handler);
            var facade = new LlmProviderService(new Source(), [new VertexLlmClient(http, new Authentication())]);
            var caches = facade.BindContextCacheAsync("connection").GetAwaiter().GetResult();
            Exception failure = Assert.Throws<InferenceResourceOperationException>(() =>
                caches.GetAsync(new(Name, Model, "imported")).GetAwaiter().GetResult());
            Assert.Same(failure, Assert.Throws<InferenceResourceOperationException>(() => facade.Dispose()));
            Assert.Same(failure, Assert.Throws<InferenceResourceOperationException>(() => facade.Dispose()));
            Assert.Equal(1, content.GetType().GetProperty(nameof(RefusingContent.CleanupAttempts))!.GetValue(content));
            Assert.DoesNotContain("private-token-or-prefix", failure.ToString(), StringComparison.Ordinal);
            var roots = new WeakReference[] { new(generation), new(fixture), new(type), new(factory), new(response), new(content), new(content.GetType()) };
            for (int attempt = 0; attempt < 3; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.All(roots, root => Assert.True(root.IsAlive));
            GC.KeepAlive(facade);
            return roots;
        }
        finally { generation.Unload(); }
    }

    public static HttpResponseMessage CreateRefusedResponse() => new(HttpStatusCode.OK) { Content = new RefusingContent(Receipt) };

    [Fact]
    public void ActualForeignRefusedUploadRequestRemainsOwnedUntilFacadeIsReleased()
    {
        WeakReference[] roots = ExerciseForeignRefusedRequest();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeignRefusedRequest()
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(VertexCacheLifetimeTests).Assembly.Location)!);
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(VertexCacheLifetimeTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Assert.Equal(typeof(VertexCacheLifetimeTests).Assembly.ManifestModule.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Type type = fixture.GetType(typeof(VertexCacheLifetimeTests).FullName!, true)!;
            MethodInfo factory = type.GetMethod(nameof(CreateRefusedRequestContent))!;
            Assert.Same(fixture, factory.Module.Assembly);
            var wrap = factory.CreateDelegate<Func<HttpContent, HttpContent>>();
            using var handler = new StagingHandler { Wrap = wrap }; using var http = new HttpClient(handler);
            var source = new Source { Staging = true }; var auth = new Authentication();
            var facade = new LlmProviderService(source, [new VertexLlmClient(http, auth)]);
            var caches = facade.BindContextCacheAsync("connection").GetAwaiter().GetResult();
            Exception operation = Assert.Throws<InferenceResourceOperationException>(() => caches.CreateAsync(new()
            {
                ModelId = Model,
                Correlation = new("operation", "display"),
                TimeToLive = TimeSpan.FromHours(1),
                Messages = [new("user", "prefix") { Attachments = [new LlmAttachment { Kind = LlmBinaryKind.Image,
                    MediaType = "image/png", FileName = "image.png", Data = "abc"u8.ToArray() }] }]
            }).GetAwaiter().GetResult());
            Exception cleanup = FindCleanup(operation);
            Assert.Same(cleanup, Assert.Throws<InferenceResourceOperationException>(() => facade.Dispose()));
            Assert.Same(cleanup, Assert.Throws<InferenceResourceOperationException>(() => facade.Dispose()));
            var request = handler.Request!; object content = request.Content!;
            Assert.Same(fixture, content.GetType().Assembly); Assert.Same(generation, AssemblyLoadContext.GetLoadContext(content.GetType().Assembly));
            Assert.Equal(1, content.GetType().GetProperty(nameof(RefusingRequestContent.CleanupAttempts))!.GetValue(content));
            Assert.Equal(0, source.DisposeCalls); Assert.Equal(0, auth.DisposeCalls);
            Assert.DoesNotContain("private-token-or-prefix", operation.ToString(), StringComparison.Ordinal);
            handler.Request = null; handler.Wrap = null;
            var roots = new WeakReference[] { new(generation), new(fixture), new(type), new(factory), new(request), new(content), new(content.GetType()) };
            for (int attempt = 0; attempt < 3; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.All(roots, root => Assert.True(root.IsAlive)); GC.KeepAlive(facade);
            return roots;
        }
        finally { generation.Unload(); }
    }

    public static HttpContent CreateRefusedRequestContent(HttpContent original) => new RefusingRequestContent(original);

    private sealed class StagingHandler : HttpMessageHandler
    {
        internal Func<HttpContent, HttpContent>? Wrap { get; set; }
        internal HttpRequestMessage? Request { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("storage.googleapis.com", request.RequestUri!.Host);
            Request = request; byte[] bytes = await request.Content!.ReadAsByteArrayAsync(token);
            string name = Uri.UnescapeDataString(request.RequestUri.Query.Split('&').Single(part => part.StartsWith("name=", StringComparison.Ordinal))[5..]);
            request.Content = Wrap!(request.Content);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["bucket"] = "cache-bucket",
                    ["name"] = name,
                    ["generation"] = "42",
                    ["size"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["contentType"] = "image/png"
                }))
            };
        }
    }
    private sealed class RefusingRequestContent(HttpContent original) : HttpContent
    {
        public int CleanupAttempts { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => original.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        { if (disposing) { CleanupAttempts++; throw new InvalidOperationException("private-token-or-prefix"); } base.Dispose(disposing); }
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
        internal bool Staging { get; init; }
        public int DisposeCalls { get; private set; }
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(new()
        {
            Transport = LlmTransport.GoogleVertexAi,
            Version = "1",
            Endpoint = "https://aiplatform.googleapis.com",
            Vertex = new() { ProjectId = "project-id", Location = "global", CacheStaging = Staging ? new("cache-bucket", "prefix/") : null },
            CapabilityOverrides = new() { SupportsModelListing = false }
        });
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
        public int CleanupAttempts { get; private set; }
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
