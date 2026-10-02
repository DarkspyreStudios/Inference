using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFileOwnerTests
{
    private const string FileJson = "{\"id\":\"file-a\",\"filename\":\"a\",\"bytes\":3,\"created_at\":0,\"object\":\"file\"}";
    private static readonly int[] BorrowedCounters = [1, 1, 0, 0, 0];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task EveryRouteRetainsActualResponseRefusalThroughRepeatedRetirement(int route)
    {
        var cause = new IOException("controlled response refusal");
        var content = new RefusingContent(Body(route), cause);
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = content }));
        var adapter = new XaiFileClient(http);
        using var stream = new MemoryStream();
        Exception failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => Invoke(adapter, route, stream));
        AssertCleanup(failure, cause);
        Assert.Same(failure, await Assert.ThrowsAsync<InferenceResourceOperationException>(() => adapter.DisposeAsync().AsTask()));
        Assert.Same(failure, await Assert.ThrowsAsync<InferenceResourceOperationException>(() => Task.Run(adapter.Dispose)));
        Assert.Equal(1, content.Disposals);
        Assert.True(stream.CanRead);
        Assert.Null(typeof(XaiFileClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter));
    }

    [Fact]
    public async Task RefusedRequestDoesNotRetainBorrowedUploadSourceOrFence()
    {
        object? upload = null;
        var cause = new IOException("controlled request refusal");
        RefusingMultipart? refused = null;
        using var http = new HttpClient(new Handler(request =>
        {
            var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);
            upload = Assert.Single(multipart);
            request.Content = refused = new RefusingMultipart(multipart, cause);
            return new(HttpStatusCode.OK) { Content = new StringContent(FileJson) };
        }));
        var adapter = new XaiFileClient(http);
        using var source = new MemoryStream("abc"u8.ToArray());
        Exception failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => Invoke(adapter, 0, source));
        AssertCleanup(failure, cause);
        Assert.Same(failure, await Assert.ThrowsAsync<InferenceResourceOperationException>(() => adapter.DisposeAsync().AsTask()));
        Assert.NotNull(upload);
        Assert.Null(upload.GetType().GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(upload));
        Assert.Null(upload.GetType().GetField("_fence", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(upload));
        Assert.Equal(1, refused!.Disposals);
        Assert.True(source.CanRead);
        Assert.Equal(0, source.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualForeignOwnerLifetimeMatchesHealthyOrRefusedCleanup(bool refuse)
    {
        WeakReference[] roots = await HoldThenRelease(refuse);
        await Collect();
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SuppressMessage("Usage", "CA2201", Justification = "Controlled OOM tests prove independent cleanup without exhausting process memory.")]
    public async Task CleanupPreservesControlledOomAndCancellationWithoutRetry(bool canceledCleanup)
    {
        var primary = new OutOfMemoryException("controlled operation failure");
        using var cancellation = new CancellationTokenSource();
        Exception cause = canceledCleanup ? new OperationCanceledException("controlled cleanup cancellation", cancellation.Token)
            : new OutOfMemoryException("controlled cleanup failure");
        var content = new RefusingContent(FileJson, cause);
        bool returned = false;
        using var http = new HttpClient(new Handler(_ => { returned = true; return new(HttpStatusCode.OK) { Content = content }; }));
        var adapter = new XaiFileClient(http);
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => adapter.GetAsync(Connection(), "file-a", () =>
        { if (returned) throw primary; }, CancellationToken.None));
        Assert.Same(primary, failure.InnerExceptions[0]);
        Exception cleanup = failure.InnerExceptions[1];
        Assert.Same(cause, cleanup.InnerException);
        if (canceledCleanup)
        {
            var canceled = Assert.IsType<InferenceResourceOperationCanceledException>(cleanup);
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
            Assert.Equal(InferenceResourceOperationPhase.Cleanup, canceled.Failure.Phase);
            Assert.Same(cleanup, await Assert.ThrowsAsync<InferenceResourceOperationCanceledException>(() => adapter.DisposeAsync().AsTask()));
        }
        else
        {
            AssertCleanup(cleanup, cause);
            Assert.Same(cleanup, await Assert.ThrowsAsync<InferenceResourceOperationException>(() => adapter.DisposeAsync().AsTask()));
        }
        Assert.Equal(1, content.Disposals);
    }

    [Fact]
    public async Task RetirementDrainsIgnoringCleanupBeforeRecordingItsActualRefusal()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cause = new IOException("controlled late disposal refusal");
        var content = new BlockingContent(entered, release.Task, cause);
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = content }));
        var adapter = new XaiFileClient(http);
        Task<LlmProviderFile> operation = Task.Run(() => adapter.GetAsync(Connection(), "file-a", () => { }, CancellationToken.None));
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = adapter.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            Assert.Same(http, typeof(XaiFileClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter));
            release.SetResult();
            Exception failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => operation);
            AssertCleanup(failure, cause);
            Assert.Same(failure, await Assert.ThrowsAsync<InferenceResourceOperationException>(() => retirement));
            Assert.Equal(1, content.Disposals);
        }
        finally
        {
            release.TrySetResult();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InferenceResourceOperationException) { }
            try { await (retirement ?? adapter.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5)); } catch (InferenceResourceOperationException) { }
        }
    }

    public static HttpResponseMessage CreateForeignResponse(bool refuse) => new(HttpStatusCode.OK)
    { Content = refuse ? new RefusingContent(FileJson, new IOException("controlled foreign refusal")) : new StringContent(FileJson) };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> HoldThenRelease(bool refuse)
    {
        (WeakReference[] roots, WeakReference[] borrowed, LlmProviderService retained) = await ExerciseForeign(refuse);
        await Collect();
        Assert.All(borrowed, root => Assert.False(root.IsAlive));
        if (refuse) Assert.All(roots, root => Assert.True(root.IsAlive));
        else Assert.All(roots, root => Assert.False(root.IsAlive));
        GC.KeepAlive(retained);
        return roots;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference[], WeakReference[], LlmProviderService)> ExerciseForeign(bool refuse)
    {
        var generation = new Generation();
        var sourceGeneration = new Generation();
        try
        {
            Assembly fixture = generation.LoadFromAssemblyPath(typeof(XaiFileOwnerTests).Assembly.Location);
            Assert.Same(generation, AssemblyLoadContext.GetLoadContext(fixture));
            Assert.Equal(typeof(XaiFileOwnerTests).Module.ModuleVersionId, fixture.ManifestModule.ModuleVersionId);
            Type type = fixture.GetType(typeof(XaiFileOwnerTests).FullName!, true)!;
            MethodInfo method = type.GetMethod(nameof(CreateForeignResponse))!;
            var response = (HttpResponseMessage)method.Invoke(null, [refuse])!;
            if (refuse) Assert.Same(generation, AssemblyLoadContext.GetLoadContext(response.Content.GetType().Assembly));
            Assembly sourceAssembly = sourceGeneration.LoadFromAssemblyPath(typeof(XaiFileOwnerTests).Assembly.Location);
            Assert.Equal(fixture.ManifestModule.ModuleVersionId, sourceAssembly.ManifestModule.ModuleVersionId);
            Assert.NotSame(fixture, sourceAssembly);
            Type sourceType = sourceAssembly.GetType(typeof(XaiFileFacadeGenerationTests).FullName!, true)!;
            MethodInfo create = sourceType.GetMethod(nameof(XaiFileFacadeGenerationTests.CreateBorrowed))!;
            object[] collaborators = (object[])create.Invoke(null, [null, null])!;
            Assert.Same(sourceGeneration, AssemblyLoadContext.GetLoadContext(collaborators[0].GetType().Assembly));
            Assert.Same(sourceGeneration, AssemblyLoadContext.GetLoadContext(collaborators[1].GetType().Assembly));
            using var http = new HttpClient(new Handler(_ => response));
            using var unusedHttp = (HttpClient)collaborators[2];
            var adapter = new LlmProviderService((ILlmConnectionSource)collaborators[0], [new XaiResponsesLlmClient(http)], [(ILlmAuthenticator)collaborators[1]]);
            if (refuse)
            {
                Exception failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => adapter.GetAsync("selected", "file-a"));
                Assert.Same(failure, await Assert.ThrowsAsync<InferenceResourceOperationException>(() => adapter.DisposeAsync().AsTask()));
            }
            else
            {
                Assert.Equal("file-a", (await adapter.GetAsync("selected", "file-a")).Id);
                await adapter.DisposeAsync();
            }
            int[] counters = (int[])sourceType.GetMethod(nameof(XaiFileFacadeGenerationTests.Counters))!.Invoke(null, [collaborators])!;
            Assert.Equal(BorrowedCounters, counters);
            // The source and auth methods execute in a separate generation from the refused content.
            return ([new(generation), new(fixture), new(response), new(response.Content)],
                [new(sourceGeneration), new(sourceAssembly), new(collaborators[0]), new(collaborators[1])], adapter);
        }
        finally { generation.Unload(); sourceGeneration.Unload(); }
    }

    private static async Task Collect()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(10); }
    }

    private static void AssertCleanup(Exception failure, Exception cause)
    {
        Assert.Same(cause, failure.InnerException);
        InferenceResourceOperationFailure facts = Assert.IsType<InferenceResourceOperationException>(failure).Failure;
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, facts.Phase);
        Assert.Equal(InferenceResourceCommitOutcome.NotAttempted, facts.CommitOutcome);
        Assert.Null(facts.Reference);
    }

    private static string Body(int route) => route switch
    {
        2 => "abc",
        3 => "{\"data\":[]}",
        4 => "{\"id\":\"file-a\",\"object\":\"file\",\"deleted\":true}",
        5 => "{\"public_url\":\"https://example.invalid/file\"}",
        6 => "{\"id\":\"file-a\",\"revoked\":true}",
        _ => FileJson
    };

    private static Task Invoke(XaiFileClient adapter, int route, Stream stream) => route switch
    {
        0 => adapter.UploadAsync(Connection(), stream, "a", null, () => { }, CancellationToken.None),
        1 => adapter.GetAsync(Connection(), "file-a", () => { }, CancellationToken.None),
        2 => adapter.DownloadAsync(Connection(), "file-a", stream, null, () => { }, CancellationToken.None),
        3 => adapter.ListAsync(Connection(), new(), () => { }, CancellationToken.None),
        4 => adapter.DeleteAsync(Connection(), "file-a", () => { }, CancellationToken.None),
        5 => adapter.CreatePublicUrlAsync(Connection(), "file-a", null, () => { }, CancellationToken.None),
        6 => adapter.RevokePublicUrlAsync(Connection(), "file-a", () => { }, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };

    private static LlmConnection Connection() => new()
    {
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Version = "v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "oauth-token" } }.Serialize()
    };

    private sealed class RefusingContent(string body, Exception cause) : StringContent(body)
    {
        internal int Disposals;
        [SuppressMessage("Usage", "CA2215", Justification = "The controlled network content refuses cleanup before releasing its owned state.")]
        protected override void Dispose(bool disposing)
        { if (disposing) { Disposals++; throw cause; } base.Dispose(disposing); }
    }

    private sealed class RefusingMultipart(HttpContent inner, Exception cause) : HttpContent
    {
        internal int Disposals;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => inner.CopyToAsync(stream);
        [SuppressMessage("Usage", "CA2215", Justification = "The controlled network wrapper retains its actual owned multipart and refuses cleanup before release.")]
        protected override void Dispose(bool disposing)
        { if (disposing) { Disposals++; GC.KeepAlive(inner); throw cause; } base.Dispose(disposing); }
    }

    private sealed class BlockingContent(TaskCompletionSource entered, Task release, Exception cause) : StringContent(FileJson)
    {
        internal int Disposals;
        [SuppressMessage("Usage", "CA2215", Justification = "The controlled content blocks and then refuses actual cleanup before releasing state.")]
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposals++; entered.SetResult(); release.GetAwaiter().GetResult(); throw cause; }
            base.Dispose(disposing);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    private sealed class Generation() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == name.Name);
    }
}
