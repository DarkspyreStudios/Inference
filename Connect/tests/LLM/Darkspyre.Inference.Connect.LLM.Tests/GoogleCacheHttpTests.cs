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

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class GoogleCacheHttpTests
{
    private const string Secret = "cache-auth-token-that-must-not-escape";
    private const string Prefix = "private cached prefix that must not escape";
    private static readonly LlmContextCacheCreateCorrelation Correlation = new("operation", "reconcile");
    private const string ReceiptBody = "{\"name\":\"cachedContents/id\",\"model\":\"models/m\",\"expireTime\":\"2030-01-01T00:00:00Z\"}";

    [Theory]
    [InlineData("transport")]
    [InlineData("provider")]
    [InlineData("parse")]
    [InlineData("cleanup")]
    public async Task UntrustedFailureGraphsCannotExposeTokenOrPrefix(string boundary)
    {
        var failure = boundary == "provider"
            ? (Exception)new LlmProviderException(Secret + Prefix, LlmTransport.GoogleGeminiApi,
                HttpStatusCode.Forbidden, sanitizedResponseBody: Secret + Prefix,
                innerException: new InvalidOperationException(Secret + Prefix))
            : new HttpRequestException(Secret + Prefix, new IOException(Secret + Prefix));
        int sends = 0;
        using var handler = new Handler((_, _) =>
        {
            sends++;
            if (boundary is "transport" or "provider") return Task.FromException<HttpResponseMessage>(failure);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = boundary == "cleanup" ? new FaultContent(ReceiptBody, failure) : new StringContent(Secret + Prefix)
            });
        });
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://generativelanguage.googleapis.com/v1beta/cachedContents");
        var error = await Assert.ThrowsAsync<LlmContextCacheCreateUncertainException>(() =>
            GoogleCacheHttp.CreateAsync(http, request, LlmTransport.GoogleGeminiApi, Correlation,
                Parse, _ => { }, CancellationToken.None));
        Assert.Equal(1, sends);
        Assert.Same(Correlation, error.Correlation);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Prefix, error.ToString(), StringComparison.Ordinal);
        Assert.All(Errors(error), item =>
        {
            Assert.DoesNotContain(Secret, item.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Prefix, item.Message, StringComparison.Ordinal);
            if (item is LlmProviderException provider) Assert.Null(provider.SanitizedResponseBody);
        });
    }

    [Fact]
    public async Task PreDispatchCancellationIsOrdinaryAndDoesNotSendOrParse()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No dispatch."));
        using var http = new HttpClient(handler);
        using var request = Request();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(http, request,
            _ => throw new InvalidOperationException("No parse."), _ => { }, stop.Token));
        Assert.IsNotType<LlmContextCacheCreateCancelledException>(error);
        Assert.Equal(stop.Token, error.CancellationToken);
    }

    [Fact]
    public async Task PreDispatchOwnerFenceIsOrdinaryAndDoesNotSend()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("No dispatch."));
        using var http = new HttpClient(handler);
        using var request = Request();
        var original = new OperationCanceledException("Owned cancellation.");
        Assert.Same(original, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(http, request,
            Parse, _ => throw original, CancellationToken.None)));
    }

    [Fact]
    public async Task SuccessUsesOneSendAndDisposesOnlyOwnedResponse()
    {
        int sends = 0, fences = 0;
        var content = new TrackedContent(ReceiptBody);
        using var handler = new Handler((request, _) =>
        {
            sends++;
            Assert.Equal("selected-key", request.Headers.GetValues("x-goog-api-key").Single());
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.Add("x-request-id", "observed-id");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var request = Request();
        request.Headers.Add("x-goog-api-key", "selected-key");
        var receipt = await Create(http, request, Parse, _ => fences++, CancellationToken.None);
        Assert.Equal("cachedContents/id", receipt.Reference.ResourceName);
        Assert.Equal(1, sends);
        Assert.Equal(1, content.Disposals);
        Assert.Equal(0, handler.Disposals);
        Assert.True(fences >= 4);
        Assert.Equal("selected-key", request.Headers.GetValues("x-goog-api-key").Single());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task RejectedCreateKeepsStatusRequestIdAndNeverReadsMaliciousBody(int status)
    {
        var content = new TrackedContent(Secret + Prefix);
        using var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
            response.Headers.Add("x-request-id", "observed-id");
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(3));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var request = Request();
        var error = await Assert.ThrowsAsync<LlmContextCacheCreateUncertainException>(() => Create(http, request,
            _ => throw new InvalidOperationException("No parse."), _ => { }, CancellationToken.None));
        var provider = Assert.IsType<LlmProviderException>(error.InnerException);
        Assert.Equal((HttpStatusCode)status, provider.StatusCode);
        Assert.Equal("observed-id", provider.RequestId);
        Assert.Equal("observed-id", error.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(3), provider.RetryAfter);
        Assert.Null(provider.SanitizedResponseBody);
        Assert.Equal(0, content.Reads);
        Assert.Equal(1, content.Disposals);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Prefix, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParserExceptionsAreNotTrustedByProviderExceptionType(bool providerError)
    {
        Exception original = providerError
            ? new LlmProviderException(Secret + Prefix, LlmTransport.GoogleVertexAi,
                providerCode: Secret, requestId: Prefix, sanitizedResponseBody: Secret + Prefix)
            : new JsonException(Secret + Prefix);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ReceiptBody) }));
        using var http = new HttpClient(handler);
        using var request = Request();
        var error = await Assert.ThrowsAsync<LlmContextCacheCreateUncertainException>(() => Create(http, request,
            _ => throw original, _ => { }, CancellationToken.None));
        var safe = Assert.IsType<LlmProviderException>(error.InnerException);
        Assert.NotSame(original, safe);
        Assert.Null(safe.ProviderCode);
        Assert.Null(safe.RequestId);
        Assert.Null(safe.SanitizedResponseBody);
        Assert.Null(safe.InnerException);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Prefix, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAndCleanupFailuresPreserveBothSafeCategoriesWithoutRetry()
    {
        var content = new TrackedContent(ReceiptBody,
            readFailure: new IOException(Secret + Prefix), cleanupFailure: new IOException(Secret + Prefix));
        int sends = 0;
        using var handler = new Handler((_, _) =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        using var request = Request();
        var error = await Assert.ThrowsAsync<LlmContextCacheCreateUncertainException>(() => Create(http, request,
            Parse, _ => { }, CancellationToken.None));
        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Contains("receipt", aggregate.InnerExceptions[0].Message, StringComparison.Ordinal);
        var cleanup = Assert.IsType<InferenceResourceOperationException>(aggregate.InnerExceptions[1]);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
        Assert.Contains("cleanup", Assert.IsType<LlmProviderException>(cleanup.InnerException).Message, StringComparison.Ordinal);
        Assert.Equal(1, sends);
        Assert.Equal(1, content.Disposals);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Prefix, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidLateReceiptIsEvidenceOnlyWhenOwnerStopsBeforePublication()
    {
        using var owner = new LlmOperationLifetime();
        Task? retirement = null;
        var lease = owner.Enter(CancellationToken.None);
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ReceiptBody) }));
        using var http = new HttpClient(handler);
        using var request = Request();
        try
        {
            var error = await Assert.ThrowsAsync<LlmContextCacheCreateCancelledException>(() => Create(http, request,
                root => { var receipt = Parse(root); retirement = owner.RetireAsync(); return receipt; },
                token => Fence(owner, token), lease.Token));
            Assert.Equal("cachedContents/id", error.ObservedReceipt!.Reference.ResourceName);
            Assert.Same(Correlation, error.Correlation);
            Assert.False(retirement!.IsCompleted);
        }
        finally
        {
            retirement ??= owner.RetireAsync();
            lease.Dispose();
            await retirement;
        }
    }

    [Fact]
    public async Task CleanupFailureCannotTurnObservedReceiptIntoSuccess()
    {
        var content = new TrackedContent(ReceiptBody, cleanupFailure: new IOException(Secret + Prefix));
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = new HttpClient(handler);
        using var request = Request();
        var error = await Assert.ThrowsAsync<LlmContextCacheCreateUncertainException>(() => Create(http, request,
            Parse, _ => { }, CancellationToken.None));
        Assert.Equal("cachedContents/id", error.ObservedReceipt!.Reference.ResourceName);
        Assert.Equal(1, content.Disposals);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Prefix, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedOwnerTokenPropagationStillRefusesLateSendOrReadAndDrains(bool heldRead)
    {
        using var caller = new CancellationTokenSource();
        var owner = new LlmOperationLifetime();
        var lease = owner.Enter(caller.Token);
        var anchor = owner.Enter(CancellationToken.None);
        var entered = Signal();
        var release = Signal();
        var callbackEntered = Signal();
        var callbackRelease = Signal();
        var content = new TrackedContent(ReceiptBody, heldRead ? entered : null, heldRead ? release : null);
        using var handler = new Handler(async (_, _) =>
        {
            if (!heldRead) { entered.TrySetResult(); await release.Task; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        using var request = Request();
        CancellationTokenRegistration registration = default;
        Task<LlmContextCache>? operation = null;
        Task? retirement = null;
        try
        {
            registration = anchor.Token.Register(() =>
            { callbackEntered.TrySetResult(); callbackRelease.Task.GetAwaiter().GetResult(); });
            operation = Create(http, request, Parse, token => Fence(owner, token), lease.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = owner.RetireAsync();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(lease.Token.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            release.TrySetResult();
            var error = await Assert.ThrowsAsync<LlmContextCacheCreateCancelledException>(() => operation);
            Assert.Null(error.ObservedReceipt);
            Assert.False(retirement.IsCompleted);
            Assert.Equal(1, content.Disposals);
            Assert.Equal(0, handler.Disposals);
            Assert.Throws<ObjectDisposedException>(() => owner.Enter(CancellationToken.None));
        }
        finally
        {
            retirement ??= owner.RetireAsync();
            callbackRelease.TrySetResult();
            release.TrySetResult();
            try { if (operation is not null) await IgnoreFailure(operation); }
            finally
            {
                lease.Dispose();
                anchor.Dispose();
                try { await retirement; }
                finally { registration.Dispose(); }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCallerCancellationAfterDispatchIsReportedWithoutRawCancellation(bool ignoring)
    {
        using var stop = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        using var handler = new Handler(async (_, token) =>
        {
            entered.TrySetResult();
            if (ignoring) await release.Task;
            else await release.Task.WaitAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReceiptBody) };
        });
        using var http = new HttpClient(handler);
        using var request = Request();
        Task<LlmContextCache>? operation = null;
        try
        {
            operation = Create(http, request, Parse, _ => { }, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            if (ignoring) Assert.False(operation.IsCompleted);
            release.TrySetResult();
            var error = await Assert.ThrowsAsync<LlmContextCacheCreateCancelledException>(() => operation);
            Assert.Equal(stop.Token, error.CancellationToken);
            Assert.Null(error.ObservedReceipt);
        }
        finally { release.TrySetResult(); if (operation is not null) await IgnoreFailure(operation); }
    }

    [Fact]
    public async Task MaliciousCancellationMessageIsReplacedAfterDispatch()
    {
        using var handler = new Handler((_, _) => Task.FromException<HttpResponseMessage>(
            new OperationCanceledException(Secret + Prefix, new IOException(Secret + Prefix))));
        using var http = new HttpClient(handler);
        using var request = Request();
        var error = await Assert.ThrowsAsync<LlmContextCacheCreateCancelledException>(() => Create(http, request,
            Parse, _ => { }, CancellationToken.None));
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Prefix, error.ToString(), StringComparison.Ordinal);
        Assert.IsType<OperationCanceledException>(error.InnerException);
        Assert.Null(error.InnerException!.InnerException);
    }

    [Fact]
    public void ActualForeignCreateAndReceiptCodecCollectWhileDefaultBorrowedClientRemainsAlive()
    {
        using var handler = new Handler(static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ReceiptBody) }));
        using var http = new HttpClient(handler);
        int fences = 0;
        Action<CancellationToken> fence = _ => fences++;
        WeakReference[] roots = ExerciseForeign(http, fence);
        Assert.True(fences >= 4);
        Assert.Equal(0, handler.Disposals);
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(10);
        }
        Assert.All(roots, root => Assert.False(root.IsAlive));
        GC.KeepAlive(http);
        GC.KeepAlive(fence);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeign(HttpClient http, Action<CancellationToken> fence)
    {
        var generation = new Generation(Path.GetDirectoryName(typeof(GoogleCacheHttpTests).Assembly.Location)!);
        var roots = new List<WeakReference> { new(generation) };
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(GoogleCacheHttpTests).Assembly.Location);
            Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
            Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
            foreach ((Assembly source, Assembly actual) in new[]
                { (typeof(GoogleCacheHttpTests).Assembly, fixture), (typeof(ConnectLlmJson).Assembly, connect), (typeof(InferenceJson).Assembly, common) })
            {
                Assert.NotSame(source, actual);
                Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
                roots.Add(new(actual));
            }
            Type caller = fixture.GetType(typeof(GoogleCacheHttpTests).FullName!, throwOnError: true)!;
            MethodInfo method = caller.GetMethod(nameof(ExerciseCreateInOwningGeneration))!;
            roots.Add(new(caller));
            roots.Add(new(method));
            var operation = (Task<WeakReference[]>)method.Invoke(null, [http, fence])!;
            roots.Add(new(operation));
            roots.AddRange(operation.GetAwaiter().GetResult());
        }
        finally { generation.Unload(); }
        return roots.ToArray();
    }

    public static async Task<WeakReference[]> ExerciseCreateInOwningGeneration(HttpClient http, Action<CancellationToken> fence)
    {
        AssemblyLoadContext generation = AssemblyLoadContext.GetLoadContext(typeof(GoogleCacheHttpTests).Assembly)!;
        if (!generation.IsCollectible || !ReferenceEquals(generation, AssemblyLoadContext.GetLoadContext(typeof(GoogleCacheHttp).Assembly))
            || !ReferenceEquals(generation, AssemblyLoadContext.GetLoadContext(typeof(LlmExplicitCacheReference).Assembly)))
            throw new InvalidOperationException("The helper, Common and fixture must belong to the actual private generation.");
        using var request = Request();
        Task<LlmContextCache> operation = Create(http, request, Parse, fence, CancellationToken.None);
        LlmContextCache receipt = await operation;
        var metadata = ConnectLlmJson.LlmContextCache;
        var referenceMetadata = InferenceJson.LlmExplicitCacheReference;
        if (!ReferenceEquals(metadata.Type.Assembly, typeof(GoogleCacheHttp).Assembly)
            || !ReferenceEquals(referenceMetadata.Type.Assembly, typeof(LlmExplicitCacheReference).Assembly)
            || !ReferenceEquals(metadata.Properties.Single(property => property.Name == nameof(LlmContextCache.Reference)).PropertyType.Assembly,
                typeof(LlmExplicitCacheReference).Assembly))
            throw new InvalidOperationException("Actual receipt and nested Common metadata must use their owning assemblies.");
        LlmContextCache roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(receipt, metadata), metadata)!;
        if (roundTrip.Reference.ResourceName != "cachedContents/id" || roundTrip.Reference.ModelId != "m")
            throw new InvalidOperationException("The actual private create/parse/codec path did not execute correctly.");
        return [new(typeof(GoogleCacheHttp)), new(metadata), new(referenceMetadata), new(operation), new(receipt),
            new(receipt.Reference), new(roundTrip)];
    }

    private sealed class Generation(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is not ("Darkspyre.Inference.Common" or "Darkspyre.Inference.Connect.LLM" or "Darkspyre.Inference.Connect.LLM.Tests")) return null;
            return LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll"));
        }
    }

    private static Task<LlmContextCache> Create(HttpClient http, HttpRequestMessage request,
        Func<JsonElement, LlmContextCache> parse, Action<CancellationToken> fence, CancellationToken token) =>
        GoogleCacheHttp.CreateAsync(http, request, LlmTransport.GoogleGeminiApi, Correlation, parse, fence, token);

    private static HttpRequestMessage Request() => new(HttpMethod.Post,
        "https://generativelanguage.googleapis.com/v1beta/cachedContents");

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Fence(LlmOperationLifetime owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { owner.ThrowIfStopping(); }
        catch (ObjectDisposedException) { throw new OperationCanceledException("Owned cache operation retired.", token); }
    }

    private static async Task IgnoreFailure(Task operation)
    {
        try { await operation; }
        catch (LlmContextCacheCreateCancelledException) { }
        catch (LlmContextCacheCreateUncertainException) { }
    }

    private static IEnumerable<Exception> Errors(Exception root)
    {
        yield return root;
        if (root is AggregateException aggregate)
            foreach (Exception error in aggregate.InnerExceptions)
                foreach (Exception nested in Errors(error)) yield return nested;
        else if (root.InnerException is { } inner)
            foreach (Exception error in Errors(inner)) yield return error;
    }

    private static LlmContextCache Parse(JsonElement root) => GoogleCacheData.Receipt(root, "scope", _ => { }, _ => "m", vertex: false);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }

    private sealed class TrackedContent(string text, TaskCompletionSource? entered = null, TaskCompletionSource? release = null,
        Exception? readFailure = null, Exception? cleanupFailure = null) : HttpContent
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
            if (disposing && cleanupFailure is not null) throw cleanupFailure;
        }
    }

    private sealed class FaultContent(string text, Exception error) : StringContent(text)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) throw error;
        }
    }
}
