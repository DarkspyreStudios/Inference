using System.Net;
using System.Text;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class XaiFileLifetimeTests
{
    private const string FileJson = "{\"id\":\"file-a\",\"filename\":\"a\",\"bytes\":3,\"created_at\":0,\"object\":\"file\"}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementDrainsIgnoringHttpAndSharesActualCancellationFailure(bool fail)
    {
        var entered = Signal();
        var released = Signal();
        var callbackEntered = Signal();
        var failure = new InvalidOperationException("controlled cancellation failure");
        using var handler = new Handler(async (_, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                callbackEntered.TrySetResult();
                if (fail) throw failure;
            });
            entered.TrySetResult();
            await released.Task;
            return Response();
        });
        using var http = new HttpClient(handler);
        var adapter = new XaiFileClient(http);
        Task<LlmProviderFile> request = adapter.GetAsync(Connection(), "file-a", () => { }, CancellationToken.None);
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = adapter.DisposeAsync().AsTask();
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(retirement, adapter.DisposeAsync().AsTask());
            Assert.False(retirement.IsCompleted);
            released.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            if (fail)
            {
                AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => retirement);
                Assert.Same(failure, Assert.Single(error.Flatten().InnerExceptions));
                Assert.Same(error, await Assert.ThrowsAsync<AggregateException>(() => adapter.DisposeAsync().AsTask()));
            }
            else await retirement;
            Assert.Equal(0, handler.Disposals);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.GetAsync(Connection(), "file-a", () => { }, CancellationToken.None));
        }
        finally
        {
            retirement ??= adapter.DisposeAsync().AsTask();
            released.TrySetResult();
            await Ignore(request);
            await Ignore(retirement);
        }
    }

    [Fact]
    public async Task OuterFenceRefusesLateHttpResponseWithoutPublishingIt()
    {
        bool stopped = false;
        int cleanup = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            stopped = true;
            return Task.FromResult(Response(() => cleanup++));
        }));
        await using var adapter = new XaiFileClient(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync(Connection(), "file-a", () =>
        {
            if (stopped) throw new OperationCanceledException("outer owner stopped");
        }, CancellationToken.None));
        Assert.Equal(1, cleanup);
    }

    [Fact]
    public async Task FinalReceiptFenceFollowsActualResponseCleanup()
    {
        bool stopped = false;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(() => stopped = true))));
        await using var adapter = new XaiFileClient(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.GetAsync(Connection(), "file-a", () =>
        {
            if (stopped) throw new OperationCanceledException("outer owner stopped during cleanup");
        }, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadRetirementWaitsIgnoringBorrowedWriteAndPublishesNoReceipt()
    {
        using var destination = new GatedDestination();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("abc")) })));
        var adapter = new XaiFileClient(http);
        Task<ResourceDownloadReceipt> transfer = adapter.DownloadAsync(Connection(), "file-a", destination, null, () => { }, CancellationToken.None);
        Task? retirement = null;
        try
        {
            await destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = adapter.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            destination.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer);
            await retirement;
            Assert.Equal("abc", Encoding.UTF8.GetString(destination.ToArray()));
            Assert.Equal(0, destination.Disposals);
            Assert.Equal(0, destination.Flushes);
        }
        finally
        {
            retirement ??= adapter.DisposeAsync().AsTask();
            destination.Release.TrySetResult();
            await Ignore(transfer);
            await Ignore(retirement);
        }
    }

    [Fact]
    public async Task PartialDestinationFailureKeepsWrittenBytesAndDoesNotCloseBorrowedStream()
    {
        using var destination = new GatedDestination { FailAfterWrite = true };
        destination.Release.TrySetResult();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("abc")) })));
        await using var adapter = new XaiFileClient(http);
        await Assert.ThrowsAsync<IOException>(() => adapter.DownloadAsync(Connection(), "file-a", destination, null, () => { }, CancellationToken.None));
        Assert.Equal("abc", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal(0, destination.Disposals);
        Assert.Equal(0, destination.Flushes);
    }

    [Fact]
    public async Task UploadRetirementWaitsIgnoringBorrowedReadAndReleasesRequestReferences()
    {
        using var source = new GatedSource();
        HttpRequestMessage? retainedRequest = null;
        HttpContent? retainedContent = null;
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            retainedRequest = request;
            retainedContent = Assert.Single(Assert.IsType<MultipartFormDataContent>(request.Content));
            await request.Content!.CopyToAsync(Stream.Null, CancellationToken.None);
            return Response();
        }));
        var adapter = new XaiFileClient(http);
        Task<LlmProviderFile> upload = adapter.UploadAsync(Connection(), source, "a", null, () => { }, CancellationToken.None);
        Task? retirement = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            retirement = adapter.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            source.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
            await retirement;
            Assert.Equal(0, source.Disposals);
            Assert.NotNull(retainedRequest);
            HttpContent content = Assert.IsAssignableFrom<HttpContent>(retainedContent);
            Assert.Null(content.GetType().GetField("_source", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(content));
            Assert.Null(content.GetType().GetField("_fence", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(content));
        }
        finally
        {
            retirement ??= adapter.DisposeAsync().AsTask();
            source.Release.TrySetResult();
            await Ignore(upload);
            await Ignore(retirement);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Ignore(Task task) { try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception error) when (error is OperationCanceledException or AggregateException) { } }
    private static LlmConnection Connection() => new()
    {
        Transport = LlmTransport.XaiResponses,
        Endpoint = "https://api.x.ai/v1",
        Version = "v1",
        Credential = new LlmCredential { OAuth = new() { AccessToken = "oauth-token" } }.Serialize()
    };
    private static HttpResponseMessage Response(Action? cleanup = null) => new(HttpStatusCode.OK) { Content = new Content(cleanup) };
    private sealed class Content(Action? cleanup) : StringContent(FileJson)
    {
        protected override void Dispose(bool disposing) { if (disposing) cleanup?.Invoke(); base.Dispose(disposing); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        public int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private sealed class GatedDestination : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public bool FailAfterWrite { get; init; }
        public int Disposals { get; private set; }
        public int Flushes { get; private set; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            Write(buffer.Span);
            if (FailAfterWrite) throw new IOException("controlled partial copy failure");
        }
        public override void Flush() { Flushes++; }
        public override Task FlushAsync(CancellationToken cancellationToken) { Flushes++; return Task.CompletedTask; }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    private sealed class GatedSource : MemoryStream
    {
        public GatedSource() : base(Encoding.UTF8.GetBytes("abc")) { }
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public int Disposals { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
            return Read(buffer.Span);
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
}
