using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GeminiCacheReadsTests
{
    private const string Receipt = """{"name":"cachedContents/id","model":"models/m","expireTime":"2030-01-01T00:00:00Z"}""";
    private const string Secret = "private-key-prefix";

    [Fact]
    public async Task ExplicitGetUsesFixedRouteAndRebindsImportedScopeFromActualReceipt()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/cachedContents/id", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("selected-key", request.Headers.GetValues("x-goog-api-key").Single());
            Assert.Null(request.Content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Receipt) });
        });
        using var http = new HttpClient(handler);
        var result = await Get(http, Connection(), Reference(), "current-scope", _ => { }, CancellationToken.None);
        Assert.Equal("current-scope", result.Reference.ScopeId);
        Assert.Equal("cachedContents/id", result.Reference.ResourceName);
        Assert.Equal("m", result.Reference.ModelId);
        Assert.Equal(DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), result.ExpiresAt);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(0, handler.Disposals);
    }

    [Fact]
    public async Task ListReadsOnePageWithExplicitSizeAndOpaqueToken()
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/cachedContents?pageSize=3&pageToken=a%2B%2F%25%26", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"cachedContents\":[" + Receipt + "],\"nextPageToken\":\"next\"}") });
        });
        using var http = new HttpClient(handler);
        var page = await List(http, Connection(), new(3, "a+/%&"), "current-scope", _ => { }, CancellationToken.None);
        Assert.Equal("current-scope", Assert.Single(page.Items).Reference.ScopeId);
        Assert.Equal("next", page.NextPageToken);
        Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task CustomEndpointIsRefusedBeforeHttp()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No send."));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<NotSupportedException>(() => Get(http, Connection() with { Endpoint = "https://custom.example" },
            Reference(), "current-scope", _ => { }, CancellationToken.None));
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cachedContents/")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/cachedContents/id")]
    [InlineData("projects/p/locations/l/cachedContents/id")]
    [InlineData("cachedContents/a/b")]
    [InlineData("cachedContents/.")]
    [InlineData("cachedContents/..")]
    [InlineData("cachedContents/a\\b")]
    [InlineData("cachedContents/a\nsecret")]
    public async Task InvalidResourceNamesRefuseBeforeSend(string name)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No send."));
        using var http = new HttpClient(handler);
        var reference = Reference() with { ResourceName = name.Length == 0 ? "invalid" : name };
        await Assert.ThrowsAsync<ArgumentException>(() => Get(http, Connection(), reference, "scope", _ => { }, CancellationToken.None));
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData("models/m")]
    [InlineData("m/x")]
    [InlineData("m?key=secret")]
    [InlineData("M")]
    public async Task InvalidModelNamesRefuseBeforeSend(string model)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No send."));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => Get(http, Connection(), Reference() with { ModelId = model },
            "scope", _ => { }, CancellationToken.None));
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData("a+%?#&", "a%2B%25%3F%23%26")]
    [InlineData("%2F", "%252F")]
    [InlineData("unicode-\u03b1", "unicode-%CE%B1")]
    public async Task OpaqueIdsRemainDataEncodedOnce(string id, string encoded)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/cachedContents/" + encoded, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { name = "cachedContents/" + id, model = "models/m", expireTime = "2030-01-01T00:00:00Z" })) });
        });
        using var http = new HttpClient(handler);
        var result = await Get(http, Connection() with { Endpoint = GeminiApiLlmClient.DefaultEndpoint + "///" },
            Reference() with { ResourceName = "cachedContents/" + id }, "scope", _ => { }, CancellationToken.None);
        Assert.Equal("cachedContents/" + id, result.Reference.ResourceName);
    }

    [Theory]
    [InlineData("wrong-name")]
    [InlineData("wrong-model")]
    [InlineData("url-name")]
    [InlineData("bare-model")]
    [InlineData("invalid-expiry")]
    public async Task PositiveGetCannotPublishContradictoryReceipt(string field)
    {
        string body = field switch
        {
            "wrong-name" => Receipt.Replace("cachedContents/id", "cachedContents/other", StringComparison.Ordinal),
            "wrong-model" => Receipt.Replace("models/m", "models/other", StringComparison.Ordinal),
            "url-name" => Receipt.Replace("cachedContents/id", "https://evil.example/" + Secret, StringComparison.Ordinal),
            "bare-model" => Receipt.Replace("models/m", Secret, StringComparison.Ordinal),
            _ => Receipt.Replace("2030-01-01T00:00:00Z", Secret, StringComparison.Ordinal)
        };
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Get(http, Connection(), Reference(), "scope", _ => { }, CancellationToken.None));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidAuthAndForeignConnectionControlsRefuseBeforeSend(bool list)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No send."));
        using var http = new HttpClient(handler);
        LlmConnection[] invalid =
        [
            Connection() with { Transport = LlmTransport.OpenAiResponses },
            Connection() with { Endpoint = GeminiApiLlmClient.DefaultEndpoint + "/v1beta" },
            Connection() with { Endpoint = GeminiApiLlmClient.DefaultEndpoint + "?key=" + Secret },
            Connection() with { AdditionalHeaders = new Dictionary<string,string> { ["x-extra"] = "value" } },
            Connection() with { Credential = new LlmCredential { ApiKey = "k", OAuth = new() { AccessToken = "a" } }.Serialize() },
            Connection() with { Credential = null },
            Connection() with { Credential = new LlmCredential { ApiKey = Secret + "\r\nx-extra: value" }.Serialize() }
        ];
        foreach (LlmConnection connection in invalid)
        {
            Exception error = await Assert.ThrowsAnyAsync<Exception>(() => Read(list, http, connection, _ => { }, CancellationToken.None));
            Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArgumentsCancellationAndOwnerAdmissionAreEffectFree(bool list)
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No send."));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentNullException>(() => Read(list, null!, Connection(), _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Read(list, http, null!, _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Read(list, http, Connection(), null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => list
            ? List(http, Connection(), new(1), " ", _ => { }, CancellationToken.None)
            : Get(http, Connection(), Reference(), " ", _ => { }, CancellationToken.None));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(list, http, Connection(), _ => { }, canceled.Token));
        var original = new OperationCanceledException("owner retired");
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(list, http, Connection(), _ => throw original, CancellationToken.None));
        Assert.Same(original, error);
        Assert.Equal(0, handler.Sends);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public async Task ListPreservesDocumentedPageBoundAndEmptyPage(int size)
    {
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("?pageSize=" + size.ToString(System.Globalization.CultureInfo.InvariantCulture), request.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        });
        using var http = new HttpClient(handler);
        var page = await List(http, Connection(), new(size), "scope", _ => { }, CancellationToken.None);
        Assert.Empty(page.Items); Assert.Null(page.NextPageToken);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => List(http, Connection(), new(1001), "scope", _ => { }, CancellationToken.None));
        Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task ListPreservesOrderedUnknownModelsAndActualNullableFacts()
    {
        const string body = """{"cachedContents":[{"name":"cachedContents/second","model":"models/unlisted-model","expireTime":"2030-01-02T00:00:00Z","usageMetadata":{"totalTokenCount":0}},{"name":"cachedContents/first","model":"models/m","expireTime":"2030-01-01T00:00:00Z"}],"nextPageToken":"next+/%"}""";
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        using var http = new HttpClient(handler);
        var page = await List(http, Connection(), new(3), "caller-scope", _ => { }, CancellationToken.None);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("cachedContents/second", page.Items[0].Reference.ResourceName);
        Assert.Equal("unlisted-model", page.Items[0].Reference.ModelId);
        Assert.Equal(0, page.Items[0].Usage!.TotalTokens);
        Assert.Null(page.Items[0].Usage!.TextCharacters);
        Assert.Null(page.Items[0].CreatedAt); Assert.Null(page.Items[0].UpdatedAt);
        Assert.Equal("cachedContents/first", page.Items[1].Reference.ResourceName);
        Assert.Null(page.Items[1].Usage); Assert.Equal("next+/%", page.NextPageToken);
        Assert.Throws<NotSupportedException>(() => ((IList<LlmContextCache>)page.Items).Clear());
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData("cachedContents/a/b", "models/m")]
    [InlineData("https://evil.example/secret", "models/m")]
    [InlineData("cachedContents/id", "m")]
    [InlineData("cachedContents/id", "models/m/x")]
    public async Task ListDoesNotPublishPartialPagesWithInvalidNamesOrModels(string name, string model)
    {
        string bad = JsonSerializer.Serialize(new { name, model, expireTime = "2030-01-01T00:00:00Z" });
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"cachedContents\":[" + Receipt + "," + bad + "]}") }));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => List(http, Connection(), new(3), "scope", _ => { }, CancellationToken.None));
        Assert.Null(error.InnerException); Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdapterOwnsOnlyItsConstructedRequestAndResponse(bool list)
    {
        HttpRequestMessage? sent = null;
        var content = new Content(list ? "{}" : Receipt);
        using var handler = new Handler((request, _) =>
        { sent = request; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }); });
        using var http = new HttpClient(handler);
        var connection = Connection(); string credential = connection.Credential!;
        _ = await Read(list, http, connection, _ => { }, CancellationToken.None);
        Assert.Equal(credential, connection.Credential);
        Assert.Equal(1, content.Disposals); Assert.Equal(0, handler.Disposals);
        using var replacement = new StringContent("unused");
        Assert.Throws<ObjectDisposedException>(() => sent!.Content = replacement);
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(false, 403)]
    [InlineData(false, 404)]
    [InlineData(true, 429)]
    [InlineData(true, 503)]
    public async Task RejectedReadsHaveOrdinarySafeStatusFactsAndNoBodyRead(bool list, int code)
    {
        var content = new Content(Secret);
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)code) { Content = content };
            response.Headers.Add("x-request-id", "actual-request");
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(3));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<LlmProviderException>(() => Read(list, http, Connection(), _ => { }, CancellationToken.None));
        Assert.Equal((HttpStatusCode)code, error.StatusCode);
        Assert.Equal("actual-request", error.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(3), error.RetryAfter);
        Assert.Null(error.InnerException); Assert.Null(error.SanitizedResponseBody);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, content.Reads); Assert.Equal(1, content.Disposals); Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData(false, "send")]
    [InlineData(true, "send")]
    [InlineData(false, "read")]
    [InlineData(true, "read")]
    [InlineData(false, "cleanup")]
    [InlineData(true, "cleanup")]
    public async Task DelayedOwnerCancellationFencesAndDrainsOwnedReads(bool list, string boundary)
    {
        using var caller = new CancellationTokenSource();
        var owner = new LlmOperationLifetime();
        var lease = owner.Enter(caller.Token);
        var anchor = owner.Enter(CancellationToken.None);
        var entered = Signal(); var release = Signal();
        var callbackEntered = Signal(); var callbackRelease = Signal();
        var content = new Content(list ? "{\"cachedContents\":[" + Receipt + "]}" : Receipt,
            boundary == "read" ? entered : null, boundary == "read" ? release : null,
            boundary == "cleanup" ? () => { entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); }
        : null);
        using var handler = new Handler(async (_, _) =>
        {
            if (boundary == "send") { entered.TrySetResult(); await release.Task; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        Task<object>? operation = null; Task? retirement = null;
        CancellationTokenRegistration blocker = default;
        try
        {
            blocker = anchor.Token.Register(() => { callbackEntered.TrySetResult(); callbackRelease.Task.GetAwaiter().GetResult(); });
            operation = Task.Run(() => Read(list, http, Connection(), token => Fence(owner, token), lease.Token));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = owner.RetireAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(lease.Token.IsCancellationRequested);
            Assert.False(operation.IsCompleted); Assert.False(retirement.IsCompleted);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.False(retirement.IsCompleted);
            Assert.Equal(1, content.Disposals); Assert.Equal(0, handler.Disposals);
        }
        finally
        {
            retirement ??= owner.RetireAsync();
            callbackRelease.TrySetResult(); release.TrySetResult();
            try { if (operation is not null) await Observe(operation); }
            finally
            {
                lease.Dispose(); anchor.Dispose();
                try { await retirement; } finally { blocker.Dispose(); }
            }
        }
    }

    [Fact]
    public void ActualForeignReadAdapterAndOwnedMetadataCollectWithDefaultBorrowersRetained()
    {
        var entered = Signal(); var release = Signal(); var retiring = Signal();
        int sends = 0, fences = 0;
        using var handler = new Handler(async (_, _) =>
        {
            int send = Interlocked.Increment(ref sends);
            if (send == 1) { entered.TrySetResult(); await release.Task; }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new Content(send == 3 ? "{\"cachedContents\":[" + Receipt + "],\"nextPageToken\":\"next\"}" : Receipt) };
        });
        using var http = new HttpClient(handler);
        Action<CancellationToken> fence = _ => fences++;
        WeakReference[] roots = ExerciseForeign(http, fence, entered, release, retiring);
        Assert.Equal(3, sends); Assert.True(fences >= 9); Assert.Equal(0, handler.Disposals);
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
        GC.KeepAlive(http); GC.KeepAlive(fence); GC.KeepAlive(entered); GC.KeepAlive(release); GC.KeepAlive(retiring);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign(HttpClient http, Action<CancellationToken> fence,
        TaskCompletionSource entered, TaskCompletionSource release, TaskCompletionSource retiring)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(GeminiCacheReadsTests).Assembly.Location)!);
        List<WeakReference> roots = [new(generation)];
        Task<WeakReference[]>? operation = null;
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(GeminiCacheReadsTests).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            foreach ((Assembly source, Assembly actual) in new[]
                { (typeof(GeminiCacheReadsTests).Assembly, fixture), (typeof(ConnectLlmJson).Assembly, connect), (typeof(InferenceJson).Assembly, common) })
            {
                Assert.NotSame(source, actual); Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual)); roots.Add(new(actual));
            }
            Type caller = fixture.GetType(typeof(GeminiCacheReadsTests).FullName!, true)!;
            MethodInfo method = caller.GetMethod(nameof(ExerciseOwningGeneration))!;
            roots.Add(new(caller)); roots.Add(new(method));
            operation = (Task<WeakReference[]>)method.Invoke(null, [http, fence, entered, retiring])!;
            retiring.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.False(operation.IsCompleted); release.TrySetResult(); roots.Add(new(operation));
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

    public static async Task<WeakReference[]> ExerciseOwningGeneration(HttpClient http, Action<CancellationToken> fence,
        TaskCompletionSource entered, TaskCompletionSource retiring)
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(GeminiCacheReadsTests).Assembly)!;
        if (!generation.IsCollectible || !ReferenceEquals(generation, AssemblyLoadContext.GetLoadContext(typeof(GeminiCacheReads).Assembly))
            || !ReferenceEquals(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmExplicitCacheReference).Assembly)))
            throw new InvalidOperationException("The actual fixture, adapter and Common must execute in the private generation.");
        var owner = new LlmOperationLifetime(); var lease = owner.Enter(CancellationToken.None);
        Action<CancellationToken> ownedFence = token => { fence(token); Fence(owner, token); };
        Task<LlmContextCache>? held = null; Task? retirement = null; Exception? caught = null;
        try
        {
            held = Get(http, Connection(), Reference(), "scope", ownedFence, lease.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = owner.RetireAsync();
            if (retirement.IsCompleted || held.IsCompleted) throw new InvalidOperationException("Ignoring read must remain admitted while retiring.");
            retiring.TrySetResult();
            try { await held; throw new InvalidOperationException("Retired read cannot publish a receipt."); }
            catch (OperationCanceledException error) { caught = error; }
        }
        finally
        {
            retirement ??= owner.RetireAsync();
            try { if (held is not null) await Observe(held); }
            finally { lease.Dispose(); await retirement; }
        }
        Task<LlmContextCache> get = Get(http, Connection(), Reference(), "new-scope", fence, CancellationToken.None);
        var receipt = await get;
        Task<LlmContextCachePage> list = List(http, Connection(), new(1), "new-scope", fence, CancellationToken.None);
        var page = await list;
        var metadata = ConnectLlmJson.LlmContextCachePage;
        var referenceMetadata = InferenceJson.LlmExplicitCacheReference;
        var converter = metadata.Options.Converters.Single(value => value.CanConvert(typeof(LlmHostedToolKind)));
        var resolver = metadata.Options.TypeInfoResolver!;
        if (!ReferenceEquals(metadata.Type.Assembly, typeof(GeminiCacheReads).Assembly)
            || !ReferenceEquals(referenceMetadata.Type.Assembly, typeof(LlmExplicitCacheReference).Assembly)
            || !ReferenceEquals(converter.GetType().Assembly, typeof(LlmExplicitCacheReference).Assembly)
            || !ReferenceEquals(resolver.GetType().Assembly, typeof(GeminiCacheReads).Assembly)
            || !ReferenceEquals(ownedFence.Method.DeclaringType!.Assembly, typeof(GeminiCacheReadsTests).Assembly)
            || !ReferenceEquals(page.Items[0].Reference.GetType().Assembly, typeof(LlmExplicitCacheReference).Assembly))
            throw new InvalidOperationException("Actual metadata, converter, resolver, callback and nested data must use the private assemblies.");
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(page, metadata), metadata)!;
        if (receipt.Reference.ScopeId != "new-scope" || restored.Items[0].Reference != receipt.Reference || restored.NextPageToken != "next")
            throw new InvalidOperationException("Actual foreign fixed reads and owned codec facts changed.");
        return [new(typeof(GeminiCacheReads)), new(metadata), new(referenceMetadata), new(converter), new(resolver),
            new(owner), new(lease), new(ownedFence), new(held!), new(get), new(list), new(caught!),
            new(receipt), new(page), new(restored), new(receipt.Reference)];
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => name.Name is
            "Darkspyre.Inference.Common" or "Darkspyre.Inference.Connect.LLM" or "Darkspyre.Inference.Connect.LLM.Tests"
            ? LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Fence(LlmOperationLifetime owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { owner.ThrowIfStopping(); }
        catch (ObjectDisposedException) { throw new OperationCanceledException("Cache read owner retired.", token); }
    }
    private static async Task Observe(Task operation)
    {
        try { await operation; }
        catch (Exception error) when (error is OperationCanceledException or LlmProviderException or AggregateException) { }
    }

    private sealed class Content(string text, TaskCompletionSource? entered = null,
        TaskCompletionSource? release = null, Action? cleanup = null) : HttpContent
    {
        internal int Reads { get; private set; }
        internal int Disposals { get; private set; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Reads++; entered?.TrySetResult();
            if (release is not null) await release.Task;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text));
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposals++; cleanup?.Invoke(); }
            base.Dispose(disposing);
        }
    }

    private static async Task<object> Read(bool list, HttpClient http, LlmConnection connection, Action<CancellationToken> fence, CancellationToken token) =>
        list ? await List(http, connection, new(1), "scope", fence, token) : await Get(http, connection, Reference(), "scope", fence, token);

    private static Task<LlmContextCache> Get(HttpClient http, LlmConnection connection, LlmExplicitCacheReference reference,
        string scope, Action<CancellationToken> fence, CancellationToken token) =>
        GeminiCacheReads.GetAsync(http, connection, reference, scope, fence, token);

    private static Task<LlmContextCachePage> List(HttpClient http, LlmConnection connection, LlmContextCacheList request,
        string scope, Action<CancellationToken> fence, CancellationToken token) =>
        GeminiCacheReads.ListAsync(http, connection, request, scope, fence, token);

    private static LlmConnection Connection() => new()
    {
        Version = "v1",
        Transport = LlmTransport.GoogleGeminiApi,
        Endpoint = GeminiApiLlmClient.DefaultEndpoint,
        Credential = new LlmCredential { ApiKey = "selected-key" }.Serialize()
    };

    private static LlmExplicitCacheReference Reference() => new("cachedContents/id", "m", "imported-scope");

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Sends { get; private set; }
        internal int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Sends++; return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
}
