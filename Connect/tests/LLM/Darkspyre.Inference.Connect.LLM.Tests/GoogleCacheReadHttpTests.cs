using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GoogleCacheReadHttpTests
{
    private const string Secret = "cache-token-private-prefix";
    private const string ReceiptBody = "{\"name\":\"cachedContents/id\",\"model\":\"models/m\",\"expireTime\":\"2030-01-01T00:00:00Z\"}";

    [Theory]
    [InlineData(false, "transport")]
    [InlineData(true, "transport")]
    [InlineData(false, "parse")]
    [InlineData(true, "parse")]
    [InlineData(false, "cleanup")]
    [InlineData(true, "cleanup")]
    public async Task UntrustedReadFailuresAreSafeOrdinaryProviderFailures(bool list, string boundary)
    {
        var original = new LlmProviderException(Secret, LlmTransport.GoogleGeminiApi,
            HttpStatusCode.Forbidden, providerCode: Secret, sanitizedResponseBody: Secret,
            innerException: new IOException(Secret));
        var content = new Content(ReceiptBody, cleanupFailure: boundary == "cleanup" ? original : null);
        int sends = 0;
        using var handler = new Handler((_, _) =>
        {
            sends++;
            return boundary == "transport" ? Task.FromException<HttpResponseMessage>(original)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        var request = Request();
        Exception failure = (await Record.ExceptionAsync(() => Read(list, http, request,
            _ => { if (boundary == "parse") throw original; }, _ => { }, CancellationToken.None)))!;
        LlmProviderException error = boundary == "cleanup"
            ? Assert.IsType<LlmProviderException>(Assert.IsType<InferenceResourceOperationException>(failure).InnerException)
            : Assert.IsType<LlmProviderException>(failure);
        Assert.NotSame(original, error);
        Assert.Null(error.InnerException);
        Assert.Null(error.ProviderCode);
        Assert.Null(error.SanitizedResponseBody);
        Assert.DoesNotContain(Secret, failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, sends);
        Assert.Equal(boundary == "transport" ? 0 : 1, content.Disposals);
        Assert.Equal(0, handler.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParserTriggeredRetirementCannotPublishLateReceiptOrPage(bool list)
    {
        using var owner = new LlmOperationLifetime();
        var lease = owner.Enter(CancellationToken.None);
        Task? retirement = null;
        var content = new Content(ReceiptBody);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        var request = Request();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(list, http, request,
                _ => retirement = owner.RetireAsync(), token => Fence(owner, token), lease.Token));
            Assert.IsNotType<LlmContextCacheCreateCancelledException>(error);
            Assert.False(retirement!.IsCompleted);
            Assert.Equal(1, content.Disposals);
        }
        finally
        {
            retirement ??= owner.RetireAsync();
            lease.Dispose();
            await retirement;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulReadBorrowsRequestAndClientAndDisposesOnlyResponse(bool list)
    {
        var content = new Content(ReceiptBody);
        int sends = 0, parses = 0;
        using var handler = new Handler((request, _) =>
        {
            sends++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("selected-key", request.Headers.GetValues("x-goog-api-key").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        var request = Request();
        request.Headers.Add("x-goog-api-key", "selected-key");
        object result = await Read(list, http, request, _ => parses++, _ => { }, CancellationToken.None);
        if (list) Assert.Equal("cachedContents/id", Assert.Single(Assert.IsType<LlmContextCachePage>(result).Items).Reference.ResourceName);
        else Assert.Equal("cachedContents/id", Assert.IsType<LlmContextCache>(result).Reference.ResourceName);
        Assert.Equal(1, sends);
        Assert.Equal(1, parses);
        Assert.Equal(1, content.Disposals);
        Assert.Equal(0, handler.Disposals);
        Assert.Equal("selected-key", request.Headers.GetValues("x-goog-api-key").Single());
    }

    [Theory]
    [InlineData(false, 400)]
    [InlineData(true, 401)]
    [InlineData(false, 403)]
    [InlineData(true, 404)]
    [InlineData(false, 429)]
    [InlineData(true, 500)]
    public async Task FailedStatusPreservesSafeFactsWithoutReadingBodyOrRetry(bool list, int status)
    {
        var content = new Content(Secret);
        int sends = 0;
        using var handler = new Handler((_, _) =>
        {
            sends++;
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
            response.Headers.Add("x-request-id", "observed");
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var request = Request();
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Read(list, http, request,
            _ => throw new InvalidOperationException("No parse."), _ => { }, CancellationToken.None));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal("observed", error.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(2), error.RetryAfter);
        Assert.Null(error.InnerException);
        Assert.Null(error.SanitizedResponseBody);
        Assert.Equal(1, sends);
        Assert.Equal(0, content.Reads);
        Assert.Equal(1, content.Disposals);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListUsesOnePageCallbackAndPreservesFixedRequestAndActualToken()
    {
        const string page = """{"cachedContents":[{"name":"cachedContents/second","model":"models/m","expireTime":"2030-01-01T00:00:00Z"},{"name":"cachedContents/first","model":"models/m","expireTime":"2030-01-01T00:00:00Z"}],"nextPageToken":"opaque+/="}""";
        int sends = 0;
        using var handler = new Handler((request, _) =>
        {
            sends++;
            Assert.Equal("https://controlled.invalid/cache?pageSize=7&pageToken=prior%2B%2F%3D", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new Content(page) });
        });
        using var http = new HttpClient(handler);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://controlled.invalid/cache?pageSize=7&pageToken=prior%2B%2F%3D");
        var result = await GoogleCacheHttp.ListAsync(http, request, LlmTransport.GoogleVertexAi,
            root => GoogleCacheData.Page(root, Parse), _ => { }, CancellationToken.None);
        Assert.Equal("cachedContents/second", result.Items[0].Reference.ResourceName);
        Assert.Equal("cachedContents/first", result.Items[1].Reference.ResourceName);
        Assert.Equal("opaque+/=", result.NextPageToken);
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LocalCancellationOrWrongMethodDoesNotDispatch(bool list, bool wrongMethod)
    {
        using var stop = new CancellationTokenSource();
        if (!wrongMethod) stop.Cancel();
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No dispatch."));
        using var http = new HttpClient(handler);
        var request = Request();
        if (wrongMethod) request.Method = HttpMethod.Post;
        if (wrongMethod) await Assert.ThrowsAsync<ArgumentException>(() => Read(list, http, request, _ => { }, _ => { }, stop.Token));
        else
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(list, http, request, _ => { }, _ => { }, stop.Token));
            Assert.Equal(stop.Token, error.CancellationToken);
            Assert.IsNotType<LlmContextCacheCreateCancelledException>(error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAndCleanupFaultsRetainBothSafeCategories(bool list)
    {
        var content = new Content(ReceiptBody, new IOException(Secret), new IOException(Secret));
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        var request = Request();
        var error = await Assert.ThrowsAsync<AggregateException>(() => Read(list, http, request, _ => { }, _ => { }, CancellationToken.None));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains("resource facts", error.InnerExceptions[0].Message, StringComparison.Ordinal);
        var cleanup = Assert.IsType<InferenceResourceOperationException>(error.InnerExceptions[1]);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
        Assert.Contains("cleanup", Assert.IsType<LlmProviderException>(cleanup.InnerException).Message, StringComparison.Ordinal);
        Assert.Null(error.InnerExceptions[0].InnerException);
        Assert.Null(cleanup.InnerException!.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, content.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndCleanupFaultPreserveCancellationAndSafeCleanup(bool list)
    {
        var content = new Content(ReceiptBody, new OperationCanceledException(Secret), new IOException(Secret));
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        var request = Request();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(list, http, request, _ => { }, _ => { }, CancellationToken.None));
        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.IsType<OperationCanceledException>(aggregate.InnerExceptions[0]);
        var cleanup = Assert.IsType<InferenceResourceOperationException>(aggregate.InnerExceptions[1]);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
        Assert.IsType<LlmProviderException>(cleanup.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.IsNotType<LlmContextCacheCreateCancelledException>(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedBodyCannotLeakRawJsonThroughParserFailure(bool list)
    {
        var content = new Content("{\"" + Secret + "\":");
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        var request = Request();
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Read(list, http, request,
            _ => throw new InvalidOperationException("No parse."), _ => { }, CancellationToken.None));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, content.Disposals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CallerCancellationDuringSendRemainsOrdinaryAndDrainsIgnoringSend(bool list, bool ignoring)
    {
        using var stop = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        using var handler = new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            if (ignoring) await release.Task;
            else await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new Content(ReceiptBody) };
        });
        using var http = new HttpClient(handler);
        var request = Request();
        Task<object>? operation = null;
        try
        {
            operation = Read(list, http, request, _ => throw new InvalidOperationException("No parse."), _ => { }, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            if (ignoring) Assert.False(operation.IsCompleted);
            release.TrySetResult();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(stop.Token, error.CancellationToken);
            Assert.Null(error.InnerException);
            Assert.IsNotType<LlmContextCacheCreateCancelledException>(error);
        }
        finally { release.TrySetResult(); if (operation is not null) await Observe(operation); }
    }

    [Theory]
    [InlineData(false, "send")]
    [InlineData(true, "send")]
    [InlineData(false, "read")]
    [InlineData(true, "read")]
    [InlineData(false, "cleanup")]
    [InlineData(true, "cleanup")]
    public async Task DelayedOwnerTokenPropagationFencesAndDrainsIgnoringBoundaries(bool list, string boundary)
    {
        using var caller = new CancellationTokenSource();
        var owner = new LlmOperationLifetime();
        var lease = owner.Enter(caller.Token);
        var anchor = owner.Enter(CancellationToken.None);
        var entered = Signal();
        var release = Signal();
        var callbackEntered = Signal();
        var callbackRelease = Signal();
        var content = new Content(ReceiptBody, entered: boundary == "read" ? entered : null,
            release: boundary == "read" ? release : null, cleanup: boundary == "cleanup"
                ? () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
        : null);
        using var handler = new Handler(async (_, _) =>
        {
            if (boundary == "send") { entered.TrySetResult(); await release.Task; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        var request = Request();
        Task<object>? operation = null;
        Task? retirement = null;
        CancellationTokenRegistration blocker = default;
        try
        {
            blocker = anchor.Token.Register(() =>
            { callbackEntered.TrySetResult(); callbackRelease.Task.GetAwaiter().GetResult(); });
            operation = Task.Run(() => Read(list, http, request, _ => { }, token => Fence(owner, token), lease.Token));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = owner.RetireAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(lease.Token.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            Assert.False(operation.IsCompleted);
            release.TrySetResult();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.IsNotType<LlmContextCacheCreateCancelledException>(error);
            Assert.False(retirement.IsCompleted);
            Assert.Equal(1, content.Disposals);
            Assert.Equal(0, handler.Disposals);
        }
        finally
        {
            retirement ??= owner.RetireAsync();
            callbackRelease.TrySetResult();
            release.TrySetResult();
            try { if (operation is not null) await Observe(operation); }
            finally
            {
                lease.Dispose();
                anchor.Dispose();
                try { await retirement; }
                finally { blocker.Dispose(); }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualSdkConsumptionOwnerFencesParsedResultAndDrains(bool list)
    {
        var content = new Content(ReceiptBody);
        var services = new ServiceCollection();
        services.AddLlmConnect();
        services.AddHttpClient("GeminiApiLlmClient").ConfigurePrimaryHttpMessageHandler(() =>
            new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content })));
        await using var root = services.BuildServiceProvider();
        var pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using var http = pool.CreateClient("GeminiApiLlmClient");
        var request = Request();
        Task? retirement = null;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(list, http, request,
                _ => { retirement = pool.DisposeAsync().AsTask(); Assert.False(retirement.IsCompleted); }, _ => { }, CancellationToken.None));
            await retirement!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, content.Disposals);
        }
        finally { await (retirement ?? pool.DisposeAsync().AsTask()); }
    }

    [Fact]
    public void ActualForeignReadAndPageCallbacksDrainAndCollectWhileDefaultBorrowersRemainAlive()
    {
        var entered = Signal();
        var release = Signal();
        var retiring = Signal();
        int sends = 0, fences = 0;
        const string page = """{"cachedContents":[{"name":"cachedContents/id","model":"models/m","expireTime":"2030-01-01T00:00:00Z"}],"nextPageToken":"opaque"}""";
        using var handler = new Handler(async (_, _) =>
        {
            int current = Interlocked.Increment(ref sends);
            if (current == 1) { entered.TrySetResult(); await release.Task; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new Content(current == 3 ? page : ReceiptBody) };
        });
        using var http = new HttpClient(handler);
        Action<CancellationToken> fence = _ => fences++;
        WeakReference[] roots = ExerciseForeign(http, fence, entered, release, retiring);
        Assert.Equal(3, sends);
        Assert.True(fences >= 9);
        Assert.Equal(0, handler.Disposals);
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
        GC.KeepAlive(http);
        GC.KeepAlive(fence);
        GC.KeepAlive(entered);
        GC.KeepAlive(release);
        GC.KeepAlive(retiring);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign(HttpClient http, Action<CancellationToken> fence,
        TaskCompletionSource entered, TaskCompletionSource release, TaskCompletionSource retiring)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(GoogleCacheReadHttpTests).Assembly.Location)!);
        List<WeakReference> roots = [new(generation)];
        Task<WeakReference[]>? operation = null;
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(GoogleCacheReadHttpTests).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            foreach ((Assembly source, Assembly actual) in new[]
                { (typeof(GoogleCacheReadHttpTests).Assembly, fixture), (typeof(ConnectLlmJson).Assembly, connect), (typeof(InferenceJson).Assembly, common) })
            {
                Assert.NotSame(source, actual);
                Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
                roots.Add(new(actual));
            }
            Type caller = fixture.GetType(typeof(GoogleCacheReadHttpTests).FullName!, true)!;
            MethodInfo method = caller.GetMethod(nameof(ExerciseReadsInOwningGeneration))!;
            roots.Add(new(caller));
            roots.Add(new(method));
            operation = (Task<WeakReference[]>)method.Invoke(null, [http, fence, entered, retiring])!;
            retiring.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.False(operation.IsCompleted);
            release.TrySetResult();
            roots.Add(new(operation));
            roots.AddRange(operation.GetAwaiter().GetResult());
        }
        finally
        {
            release.TrySetResult();
            try { if (operation is not null) operation.GetAwaiter().GetResult(); }
            finally { generation.Unload(); }
        }
        return roots.ToArray();
    }

    public static async Task<WeakReference[]> ExerciseReadsInOwningGeneration(HttpClient http,
        Action<CancellationToken> fence, TaskCompletionSource entered, TaskCompletionSource retiring)
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(GoogleCacheReadHttpTests).Assembly)!;
        if (!generation.IsCollectible || !ReferenceEquals(generation, AssemblyLoadContext.GetLoadContext(typeof(GoogleCacheHttp).Assembly))
            || !ReferenceEquals(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmExplicitCacheReference).Assembly)))
            throw new InvalidOperationException("The actual helper, fixture and Common must execute in the private generation.");
        var owner = new LlmOperationLifetime();
        var lease = owner.Enter(CancellationToken.None);
        var heldRequest = Request();
        Task<LlmContextCache>? held = null;
        Task? retirement = null;
        Action<CancellationToken> ownedFence = token => { fence(token); Fence(owner, token); };
        try
        {
            held = GoogleCacheHttp.GetAsync(http, heldRequest, LlmTransport.GoogleGeminiApi, Parse, ownedFence, lease.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = owner.RetireAsync();
            if (retirement.IsCompleted || held.IsCompleted) throw new InvalidOperationException("Ignoring foreign read must remain admitted while retiring.");
            retiring.TrySetResult();
            try { await held; throw new InvalidOperationException("Retired read cannot publish a late receipt."); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            retirement ??= owner.RetireAsync();
            try { if (held is not null) await Observe(held); }
            finally { lease.Dispose(); await retirement; }
        }
        var getRequest = Request();
        Task<LlmContextCache> get = GoogleCacheHttp.GetAsync(http, getRequest, LlmTransport.GoogleGeminiApi, Parse, fence, CancellationToken.None);
        var receipt = await get;
        var listRequest = Request();
        Task<LlmContextCachePage> list = GoogleCacheHttp.ListAsync(http, listRequest, LlmTransport.GoogleVertexAi,
            root => GoogleCacheData.Page(root, Parse), fence, CancellationToken.None);
        var page = await list;
        var metadata = ConnectLlmJson.LlmContextCachePage;
        var referenceMetadata = InferenceJson.LlmExplicitCacheReference;
        if (!ReferenceEquals(metadata.Type.Assembly, typeof(GoogleCacheHttp).Assembly)
            || !ReferenceEquals(referenceMetadata.Type.Assembly, typeof(LlmExplicitCacheReference).Assembly)
            || !ReferenceEquals(page.Items[0].Reference.GetType().Assembly, typeof(LlmExplicitCacheReference).Assembly))
            throw new InvalidOperationException("Actual owned page metadata and nested reference must use the private assemblies.");
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(page, metadata), metadata)!;
        if (receipt.Reference.ResourceName != "cachedContents/id" || restored.Items[0].Reference != receipt.Reference
            || restored.NextPageToken != "opaque") throw new InvalidOperationException("Actual foreign read, page and codec facts changed.");
        return [new(typeof(GoogleCacheHttp)), new(metadata), new(referenceMetadata), new(owner), new(lease), new(ownedFence),
            new(held!), new(get), new(list), new(receipt), new(page), new(restored), new(receipt.Reference)];
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name is
            "Darkspyre.Inference.Common" or "Darkspyre.Inference.Connect.LLM" or "Darkspyre.Inference.Connect.LLM.Tests"
            ? LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null;
    }

    private static async Task<object> Read(bool list, HttpClient http, HttpRequestMessage request,
        Action<JsonElement> parsed, Action<CancellationToken> fence, CancellationToken token)
    {
        if (list) return await GoogleCacheHttp.ListAsync(http, request, LlmTransport.GoogleGeminiApi,
            root => { parsed(root); return new([Parse(root)], null); }, fence, token);
        return await GoogleCacheHttp.GetAsync(http, request, LlmTransport.GoogleGeminiApi,
            root => { parsed(root); return Parse(root); }, fence, token);
    }

    private static LlmContextCache Parse(JsonElement root) => GoogleCacheData.Receipt(root, "scope", _ => { }, _ => "m", vertex: false);
    private static HttpRequestMessage Request() => new(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/cachedContents/id");
    private static void Fence(LlmOperationLifetime owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { owner.ThrowIfStopping(); }
        catch (ObjectDisposedException) { throw new OperationCanceledException("Owned cache read retired.", token); }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Observe(Task operation)
    {
        try { await operation; }
        catch (Exception error) when (error is OperationCanceledException or LlmProviderException or AggregateException) { }
    }

    private sealed class Content(string text, Exception? readFailure = null, Exception? cleanupFailure = null,
        TaskCompletionSource? entered = null, TaskCompletionSource? release = null, Action? cleanup = null) : HttpContent
    {
        internal int Reads { get; private set; }
        internal int Disposals { get; private set; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Reads++;
            entered?.TrySetResult();
            if (release is not null) await release.Task;
            if (readFailure is not null) throw readFailure;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
            if (disposing) cleanup?.Invoke();
            if (disposing && cleanupFailure is not null) throw cleanupFailure;
        }
    }
}
