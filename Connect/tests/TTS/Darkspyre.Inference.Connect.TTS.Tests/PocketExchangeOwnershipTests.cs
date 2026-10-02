using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class PocketExchangeOwnershipTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task ActualExchangeRetainsEveryRefusedOwnerAndCachesOnlyCleanup(bool responseFails, bool requestFails, bool operationFails)
    {
        var responseFailure = new InvalidOperationException("response cleanup");
        var requestFailure = new InvalidOperationException("request cleanup");
        var responseBody = new RefusingContent(responseFails ? responseFailure : null);
        var requestChild = new RefusingContent(requestFails ? requestFailure : null);
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.IsType<MultipartFormDataContent>(request.Content).Add(requestChild, "owned-child");
            return new(operationFails ? HttpStatusCode.InternalServerError : HttpStatusCode.OK) { Content = responseBody };
        }));
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(new() { Endpoint = new("https://example.invalid/") });
        Exception? operation = await Record.ExceptionAsync(() => client.SpeakAsync(new() { Text = "actual" }));
        Exception? retirement = await Record.ExceptionAsync(() => client.DisposeAsync().AsTask());
        Assert.NotNull(operation); Assert.NotNull(retirement);
        var operationErrors = operation is AggregateException aggregate ? aggregate.InnerExceptions.ToArray() : [operation];
        int index = 0;
        if (operationFails) Assert.IsType<Darkspyre.Inference.Common.InferenceException>(operationErrors[index++]);
        var cleanupErrors = new List<Exception>();
        if (responseFails)
        {
            var error = Assert.IsType<InferenceResourceOperationException>(operationErrors[index++]);
            Assert.Equal(InferenceResourceOperationPhase.Cleanup, error.Failure.Phase); Assert.Same(responseFailure, error.InnerException);
            cleanupErrors.Add(error);
        }
        if (requestFails)
        {
            var error = Assert.IsType<InferenceResourceOperationException>(operationErrors[index++]);
            Assert.Equal(InferenceResourceOperationPhase.Cleanup, error.Failure.Phase); Assert.Same(requestFailure, error.InnerException);
            cleanupErrors.Add(error);
        }
        Assert.Equal(index, operationErrors.Length);
        if (cleanupErrors.Count == 1) Assert.Same(cleanupErrors[0], retirement);
        else Assert.Equal(cleanupErrors, Assert.IsType<AggregateException>(retirement).InnerExceptions);
        Assert.Same(retirement, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
        Assert.Equal(1, responseBody.DisposeCalls); Assert.Equal(1, requestChild.DisposeCalls);
    }

    [Fact]
    public async Task AlreadyCreatedBodyIsOwnedBeforeFirstSendFenceRefuses()
    {
        int sends = 0;
        using var http = new HttpClient(new Handler(_ => { sends++; throw new InvalidOperationException(); }));
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(new() { Endpoint = new("https://example.invalid/") });
        await client.DisposeAsync();
        var body = new RefusingContent(null);
        MethodInfo send = client.GetType().GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(byte[]));
        Func<HttpResponseMessage, CancellationToken, Task<byte[]>> read = (_, _) => Task.FromResult(Array.Empty<byte>());
        var pending = (Task<byte[]>)send.Invoke(client, [HttpMethod.Post, "tts", body, read, CancellationToken.None])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, body.DisposeCalls); Assert.Equal(0, sends);
    }

    [Fact]
    public void FailedResponseAndRequestRootsFollowActualCachedClientFailure()
    {
        WeakReference[] roots = RetainThenReleaseFailedRoots();
        for (int index = 0; roots.Any(root => root.IsAlive) && index < 30; index++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RetainThenReleaseFailedRoots()
    {
        (ITtsClient client, Exception failure, WeakReference[] roots) = ExerciseFailedRoots();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Assert.All(roots, root => Assert.True(root.IsAlive));
        GC.KeepAlive(client); GC.KeepAlive(failure); return roots;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ITtsClient, Exception, WeakReference[]) ExerciseFailedRoots()
    {
        HttpRequestMessage? requestOwner = null; HttpResponseMessage? responseOwner = null;
        using var http = new HttpClient(new Handler(request =>
        {
            requestOwner = request;
            Assert.IsType<MultipartFormDataContent>(request.Content).Add(new RefusingContent(new InvalidOperationException("request")), "child");
            return responseOwner = new(HttpStatusCode.OK) { Content = new RefusingContent(new InvalidOperationException("response")) };
        }));
        ITtsClient client = new PocketTtsHttpProvider(http).Bind(new() { Endpoint = new("https://example.invalid/") });
        Exception? failure = Record.Exception(() => client.SpeakAsync(new() { Text = "actual" }).GetAwaiter().GetResult());
        Assert.NotNull(failure); Assert.NotNull(Record.Exception(client.Dispose));
        WeakReference[] roots = [new(requestOwner!), new(responseOwner!)]; requestOwner = null; responseOwner = null;
        return (client, failure!, roots);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request)); }
    private sealed class RefusingContent(Exception? failure) : ByteArrayContent(PocketResourceLifetimeTests.Audio)
    {
        public int DisposeCalls { get; private set; }
        protected override void Dispose(bool disposing)
        { if (disposing) { DisposeCalls++; if (failure is not null) throw failure; } base.Dispose(disposing); }
    }
}
