using System.Net;
using System.Text.Json;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.Video.Internal;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Video.Tests;

public sealed class XaiVideoPollTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedVideoDownloadDrainsHeldReadsAndRetainsResponseCleanupFailure(bool refuseCleanup)
    {
        var body = new HeldBody(refuseCleanup);
        using var handler = new DownloadHandler(body);
        using var factory = new Factory(handler);
        var services = new ServiceCollection();
        services.AddLlmConnect(options => options.CreationMode = InferenceHttpCreationMode.BorrowHostFactory);
        services.AddVideoConnect();
        services.AddSingleton<IHttpClientFactory>(factory);
        services.AddSingleton<ILlmConnectionSource>(new Source());
        ServiceProvider container = services.BuildServiceProvider();
        AsyncServiceScope scope = container.CreateAsyncScope();
        LlmProviderService core = scope.ServiceProvider.GetRequiredService<LlmProviderService>();
        var binding = new XaiFamilyBinding(new("selected"), scope.ServiceProvider.GetRequiredService<LlmConnectionAccess>(), core);
        IVideoInference video = scope.ServiceProvider.GetRequiredService<VideoProviderService>().Bind(new()
        {
            Transport = VideoTransport.XaiImagine,
            Endpoint = "https://api.x.ai/v1",
            Xai = binding.Selection
        }, binding);
        try
        {
            VideoJobSnapshot observed = await video.GetAsync(new("job-1", new("https://api.x.ai/v1/videos", "selected")));
            Assert.Equal(VideoJobState.Completed, observed.State);
            Stream download = await video.DownloadAsync(observed.Output!);
            Task<int> read = download.ReadAsync(new byte[1]).AsTask();
            await body.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task facadeRetirement = video.DisposeAsync().AsTask();
            Task coreRetirement = core.DisposeAsync().AsTask();
            await body.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(facadeRetirement.IsCompleted);
            Assert.False(coreRetirement.IsCompleted);
            Assert.Equal(1, body.Disposals);
            Assert.Equal(1, handler.DownloadResponse!.Disposals);
            body.ReleaseRead.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));

            if (refuseCleanup)
            {
                Exception facadeFailure = await Assert.ThrowsAnyAsync<Exception>(() => facadeRetirement.WaitAsync(TimeSpan.FromSeconds(5)));
                Exception coreFailure = await Assert.ThrowsAnyAsync<Exception>(() => coreRetirement.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Contains(Causes(facadeFailure), error => error is InferenceResourceOperationException
                { Failure.Phase: InferenceResourceOperationPhase.Cleanup });
                Assert.Contains(Causes(coreFailure), error => error is InferenceResourceOperationException
                { Failure.Phase: InferenceResourceOperationPhase.Cleanup });
                Assert.Same(facadeFailure, await Assert.ThrowsAnyAsync<Exception>(() => video.DisposeAsync().AsTask()));
                Assert.Same(coreFailure, await Assert.ThrowsAnyAsync<Exception>(() => core.DisposeAsync().AsTask()));
                await Assert.ThrowsAnyAsync<Exception>(() => download.DisposeAsync().AsTask());
            }
            else
            {
                await facadeRetirement.WaitAsync(TimeSpan.FromSeconds(5));
                await coreRetirement.WaitAsync(TimeSpan.FromSeconds(5));
                await download.DisposeAsync();
            }
            Assert.Equal(1, body.Disposals);
            Assert.Equal(1, handler.DownloadResponse.Disposals);
            Assert.Equal(2, handler.Polls);
            Assert.Equal(1, handler.Downloads);
            Assert.All(factory.Clients, client => Assert.Equal(TimeSpan.FromSeconds(100), client.Timeout));
        }
        finally
        {
            body.ReleaseRead.TrySetResult();
            Exception? facadeCleanup = await Record.ExceptionAsync(() => video.DisposeAsync().AsTask());
            Exception? coreCleanup = await Record.ExceptionAsync(() => core.DisposeAsync().AsTask());
            Exception? scopeCleanup = await Record.ExceptionAsync(() => scope.DisposeAsync().AsTask());
            Exception? containerCleanup = await Record.ExceptionAsync(() => container.DisposeAsync().AsTask());
            if (!refuseCleanup)
            {
                Assert.Null(facadeCleanup);
                Assert.Null(coreCleanup);
                Assert.Null(scopeCleanup);
                Assert.Null(containerCleanup);
            }
        }
    }

    private static IEnumerable<Exception> Causes(Exception error)
    {
        yield return error;
        IEnumerable<Exception> children = error is AggregateException aggregate ? aggregate.InnerExceptions
            : error.InnerException is { } inner ? [inner] : [];
        foreach (Exception child in children)
            foreach (Exception cause in Causes(child)) yield return cause;
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new()
            {
                Version = "1",
                Transport = LlmTransport.XaiResponses,
                Endpoint = "https://api.x.ai/v1",
                Xai = new(),
                Credential = new LlmCredential
                { OAuth = new() { AccessToken = "video-test-token", AccountId = "test-account", ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() } }.Serialize()
            });
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("The supplied credential is already fresh.");
    }

    private sealed class DownloadHandler(Stream body) : HttpMessageHandler
    {
        public int Polls { get; private set; }
        public int Downloads { get; private set; }
        public CountedResponse? DownloadResponse { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri!.Host == "api.x.ai")
            {
                Polls++;
                Assert.Equal("/v1/videos/job-1", request.RequestUri.AbsolutePath);
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("""{"status":"done","video":{"duration":6,"respect_moderation":true,"url":"https://media.example.test/video"}}""") });
            }
            Downloads++;
            Assert.Equal(new Uri("https://media.example.test/video"), request.RequestUri);
            Assert.Empty(request.Headers);
            DownloadResponse = new() { Content = new StreamContent(body) };
            return Task.FromResult<HttpResponseMessage>(DownloadResponse);
        }
    }

    private sealed class CountedResponse() : HttpResponseMessage(HttpStatusCode.OK)
    {
        public int Disposals { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        public List<HttpClient> Clients { get; } = [];
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            Clients.Add(client);
            return client;
        }
        public void Dispose()
        {
            foreach (HttpClient client in Clients) client.Dispose();
        }
    }

    private sealed class HeldBody(bool refuseCleanup) : Stream
    {
        private int _closed;
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadEntered.TrySetResult();
            // A pending provider read stays admitted even when it ignores cancellation and closure.
            await ReleaseRead.Task;
            buffer.Span[0] = 42;
            return 1;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Disposals++;
                CloseEntered.TrySetResult();
                if (refuseCleanup) throw new InvalidOperationException("The controlled response body refuses cleanup.");
            }
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void AModeratedVideoWithAnEmptyUrlCompletesAndKeepsTheEmptyUrl()
    {
        XaiVideoPoll poll = XaiVideoProtocol.ParsePoll(HttpStatusCode.OK,
            """{"status":"done","video":{"duration":6,"respect_moderation":false,"url":""}}""",
            static _ => false, static () => { });

        Assert.Equal(VideoJobState.Completed, poll.State);
        JsonElement output = poll.Output!.Value;
        Assert.False(output.GetProperty("respect_moderation").GetBoolean());
        Assert.Equal(string.Empty, output.GetProperty("url").GetString());
    }
}
