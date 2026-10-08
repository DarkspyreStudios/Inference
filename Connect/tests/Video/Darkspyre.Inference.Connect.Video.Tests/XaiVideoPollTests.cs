using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Video;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.Video.Internal;
using Darkspyre.Inference.Connect.Video.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.Video.Tests;

public sealed class XaiVideoPollTests
{
    [Fact]
    public async Task ReflectedDefaultHeaderCannotBecomeVideoJobIdentity()
    {
        using var handler = new RejectedVideoHandler(false, """{"request_id":"video-header-secret"}""");
        using var factory = new Factory(handler, "video-header-secret");
        var services = new ServiceCollection();
        services.AddLlmConnect(options => options.CreationMode = InferenceHttpCreationMode.BorrowHostFactory);
        services.AddSingleton<IHttpClientFactory>(factory);
        services.AddSingleton<ILlmConnectionSource>(new Source());
        await using ServiceProvider container = services.BuildServiceProvider();
        await using AsyncServiceScope scope = container.CreateAsyncScope();
        var binding = new XaiFamilyBinding(new("selected"), scope.ServiceProvider.GetRequiredService<LlmConnectionAccess>(),
            scope.ServiceProvider.GetRequiredService<LlmProviderService>());
        using var transport = new XaiImagineVideoTransport();
        var connection = new VideoConnection
        { Transport = VideoTransport.XaiImagine, Endpoint = "https://api.x.ai/v1", Xai = binding.Selection };

        InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() =>
            transport.SubmitAsync(connection, new VideoGenerationRequest
            { ModelId = "grok-imagine-video-1.5", Prompt = "A paper boat" }, binding));

        Assert.Equal(InferenceFailureCode.ProtocolFailure, failure.Code);
        Assert.DoesNotContain("video-header-secret", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VideoFailuresRedactEffectiveHeadersWithoutLosingProviderFacts(bool sendFailure)
    {
        using var handler = new RejectedVideoHandler(sendFailure);
        using var factory = new Factory(handler, "video-header-secret");
        var services = new ServiceCollection();
        services.AddLlmConnect(options => options.CreationMode = InferenceHttpCreationMode.BorrowHostFactory);
        services.AddSingleton<IHttpClientFactory>(factory);
        services.AddSingleton<ILlmConnectionSource>(new Source());
        await using ServiceProvider container = services.BuildServiceProvider();
        await using AsyncServiceScope scope = container.CreateAsyncScope();
        var core = scope.ServiceProvider.GetRequiredService<LlmProviderService>();
        var binding = new XaiFamilyBinding(new("selected"), scope.ServiceProvider.GetRequiredService<LlmConnectionAccess>(), core);
        using var transport = new XaiImagineVideoTransport();
        var connection = new VideoConnection
        { Transport = VideoTransport.XaiImagine, Endpoint = "https://api.x.ai/v1", Xai = binding.Selection };

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            transport.SubmitAsync(connection, new VideoGenerationRequest
            { ModelId = "grok-imagine-video-1.5", Prompt = "A paper boat" }, binding));

        Assert.Equal(HttpStatusCode.TooManyRequests, failure.StatusCode);
        Assert.Equal("rate_limit_[REDACTED]_[REDACTED]", failure.ProviderCode);
        Assert.Equal("video-request-[REDACTED]-[REDACTED]", failure.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(17), failure.RetryAfter);
        Assert.Contains("Video refused", failure.Message, StringComparison.Ordinal);
        foreach (string secret in new[] { "video-test-token", "video-header-secret" })
            Assert.DoesNotContain(secret, failure.ToString() + failure.SanitizedResponseBody, StringComparison.Ordinal);
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData("21:9")]
    [InlineData("5:2")]
    public void CurrentVideoRatiosPreserveNativeBody(string aspectRatio)
    {
        var request = new VideoGenerationRequest
        { ModelId = "grok-imagine-video-1.5", Prompt = "A paper boat", AspectRatio = aspectRatio };

        (string route, JsonObject body) = XaiVideoProtocol.Build(request,
            _ => throw new InvalidOperationException("No media source is supplied."), static () => { });

        Assert.Equal("videos/generations", route);
        Assert.Equal(aspectRatio, body["aspect_ratio"]!.GetValue<string>());
        Assert.Equal(request.ModelId, body["model"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownVideoResolutionSupportAllowsNativeAttempt(bool firstFrame)
    {
        var image = new UrlMediaSource(new("https://media.example.test/image"));
        var request = new VideoGenerationRequest
        {
            ModelId = "future-video-model",
            Prompt = "A paper boat",
            ResolutionHeight = 1080,
            FirstFrame = firstFrame ? image : null
        };

        (string route, JsonObject body) = XaiVideoProtocol.Build(request, source =>
        {
            Assert.Same(image, source);
            return new() { ["url"] = image.Url.OriginalString };
        }, static () => { });

        Assert.Equal("videos/generations", route);
        Assert.Equal("1080p", body["resolution"]!.GetValue<string>());
        Assert.Equal(request.ModelId, body["model"]!.GetValue<string>());
        Assert.Equal(firstFrame, body.ContainsKey("image"));
        if (firstFrame) Assert.Equal(image.Url.OriginalString, body["image"]!["url"]!.GetValue<string>());
        Assert.Null(XaiVideoProtocol.Capabilities(request.ModelId).ResolutionHeights);

        request = request with { ModelId = "grok-imagine-video" };
        Assert.Throws<NotSupportedException>(() => XaiVideoProtocol.Build(request,
            _ => throw new InvalidOperationException("Unsupported resolution reached source resolution."), static () => { }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiteHighResolutionPreservesNativeBody(bool firstFrame)
    {
        var image = new UrlMediaSource(new("https://media.example.test/image"));
        var request = new VideoGenerationRequest
        {
            ModelId = "grok-imagine-video-1.5-lite",
            Prompt = "A paper boat",
            ResolutionHeight = 1080,
            FirstFrame = firstFrame ? image : null
        };

        (_, JsonObject body) = XaiVideoProtocol.Build(request, source =>
        {
            Assert.Same(image, source);
            return new() { ["url"] = image.Url.OriginalString };
        }, static () => { });

        Assert.Equal("1080p", body["resolution"]!.GetValue<string>());
        Assert.Equal(request.ModelId, body["model"]!.GetValue<string>());
        Assert.Equal(firstFrame, body.ContainsKey("image"));
        if (firstFrame) Assert.Equal(image.Url.OriginalString, body["image"]!["url"]!.GetValue<string>());
        VideoCapabilities capabilities = XaiVideoProtocol.Capabilities(request.ModelId);
        Assert.Collection(capabilities.ResolutionHeights!,
            height => Assert.Equal(480, height), height => Assert.Equal(720, height), height => Assert.Equal(1080, height));
        Assert.Equal(VideoOperation.Generation, Assert.Single(capabilities.Operations));
        Assert.Equal(0, capabilities.MaximumReferenceImages);
        Assert.Equal(0, capabilities.MaximumKeyframes);
        Assert.False(capabilities.SupportsLastFrame);
    }

    [Theory]
    [InlineData("grok-imagine-video-1.5", false)]
    [InlineData("grok-imagine-video-1.5", true)]
    [InlineData("grok-imagine-video-1.5-lite", false)]
    [InlineData("grok-imagine-video-1.5-lite", true)]
    public void GenerationOnlyVideoModelsRefuseOtherOperationsBeforeSourceResolution(string modelId, bool extension)
    {
        var source = new UrlMediaSource(new("https://media.example.test/video"));
        VideoRequest request = extension
            ? new VideoExtensionRequest { ModelId = modelId, Prompt = "Continue", SourceVideo = source }
            : new VideoEditRequest { ModelId = modelId, Prompt = "Change", SourceVideo = source };

        Assert.Throws<NotSupportedException>(() => XaiVideoProtocol.Build(request,
            _ => throw new InvalidOperationException("Unsupported operation reached source resolution."), static () => { }));
        VideoCapabilities capabilities = XaiVideoProtocol.Capabilities(modelId);
        Assert.DoesNotContain(extension ? VideoOperation.Extension : VideoOperation.Edit, capabilities.Operations);
        Assert.Null(capabilities.MinimumExtensionDuration);
        Assert.Null(capabilities.MaximumExtensionDuration);
    }

    [Theory]
    [InlineData("grok-imagine-video-1.5", 15)]
    [InlineData("grok-imagine-video", 8)]
    public void VideoReferenceImageLimitsRefuseBeforeSourceResolution(string modelId, int count)
    {
        var image = new UrlMediaSource(new("https://media.example.test/image"));
        var request = new VideoGenerationRequest
        { ModelId = modelId, Prompt = "A paper boat", ReferenceImages = Enumerable.Repeat<InferenceMediaSource>(image, count).ToArray() };

        Assert.Throws<ArgumentOutOfRangeException>(() => XaiVideoProtocol.Build(request,
            _ => throw new InvalidOperationException("Excess references reached source resolution."), static () => { }));
        Assert.Equal(count - 1, XaiVideoProtocol.Capabilities(modelId).MaximumReferenceImages);
    }

    [Theory]
    [InlineData("grok-imagine-video")]
    [InlineData("grok-imagine-video-1.5-lite")]
    public void VideoModelsWithoutReferenceAudioRefuseBeforeSourceResolution(string modelId)
    {
        var request = new VideoGenerationRequest
        { ModelId = modelId, Prompt = "A paper boat", ReferenceAudio = [new VideoVoiceReference("eve")] };

        Assert.Throws<NotSupportedException>(() => XaiVideoProtocol.Validate(request));
        VideoCapabilities capabilities = XaiVideoProtocol.Capabilities(modelId);
        Assert.Equal(0, capabilities.MaximumReferenceAudio);
        Assert.False(capabilities.SupportsPresetVoiceReferences);
        Assert.False(capabilities.SupportsSuppliedAudioReferences);
    }

    [Theory]
    [InlineData("last-frame")]
    [InlineData("keyframes")]
    [InlineData("reference-images")]
    public void LiteVideoRefusesReferenceModesBeforeSourceResolution(string mode)
    {
        var image = new UrlMediaSource(new("https://media.example.test/image"));
        var request = new VideoGenerationRequest
        {
            ModelId = "grok-imagine-video-1.5-lite",
            Prompt = "A paper boat",
            LastFrame = mode == "last-frame" ? image : null,
            Keyframes = mode == "keyframes" ? [new(image, TimeSpan.FromSeconds(1))] : null,
            ReferenceImages = mode == "reference-images" ? [image] : null
        };

        Assert.Throws<NotSupportedException>(() => XaiVideoProtocol.Build(request,
            _ => throw new InvalidOperationException("Unsupported references reached source resolution."), static () => { }));
    }

    [Fact]
    public void ClassicReferenceVideoRefusesDurationAboveTenSeconds()
    {
        var request = new VideoGenerationRequest
        {
            ModelId = "grok-imagine-video",
            Prompt = "A paper boat",
            Duration = TimeSpan.FromSeconds(11),
            ReferenceImages = [new UrlMediaSource(new("https://media.example.test/image"))]
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => XaiVideoProtocol.Build(request,
            _ => throw new InvalidOperationException("Invalid duration reached source resolution."), static () => { }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OwnedVideoDownloadDrainsHeldReadsAndRetainsResponseCleanupFailure(bool refuseCleanup, bool sdkOwned)
    {
        var body = new HeldBody(refuseCleanup);
        using var handler = new DownloadHandler(body);
        using var mediaHandler = new DownloadHandler(body);
        using var factory = new Factory(handler);
        var services = new ServiceCollection();
        services.AddLlmConnect(options => options.CreationMode = sdkOwned
            ? InferenceHttpCreationMode.SdkGenerationOwned : InferenceHttpCreationMode.BorrowHostFactory);
        services.AddVideoConnect();
        services.AddSingleton<IHttpClientFactory>(factory);
        if (sdkOwned)
        {
            services.AddHttpClient("XaiResponsesLlmClient").ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddHttpClient("XaiMediaDownloadClient").ConfigurePrimaryHttpMessageHandler(() => mediaHandler);
        }
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
        DownloadHandler downloadHandler = sdkOwned ? mediaHandler : handler;
        Task? generationRetirement = null;
        try
        {
            VideoJobSnapshot observed = await video.GetAsync(new("job-1", new("https://api.x.ai/v1/videos", "selected")));
            Assert.Equal(VideoJobState.Completed, observed.State);
            Stream download = await video.DownloadAsync(observed.Output!);
            Task<int> read = download.ReadAsync(new byte[1]).AsTask();
            await body.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task facadeRetirement = video.DisposeAsync().AsTask();
            Task coreRetirement = core.DisposeAsync().AsTask();
            if (sdkOwned) generationRetirement = container.DisposeAsync().AsTask();
            await body.CloseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(facadeRetirement.IsCompleted);
            Assert.False(coreRetirement.IsCompleted);
            Assert.Equal(1, body.Disposals);
            Assert.Equal(1, downloadHandler.DownloadResponse!.Disposals);
            if (sdkOwned)
            {
                Assert.False(generationRetirement!.IsCompleted);
                Assert.Equal(0, handler.Disposals);
                Assert.Equal(0, mediaHandler.Disposals);
            }
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
                if (sdkOwned)
                {
                    await generationRetirement!.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(1, handler.Disposals);
                    Assert.Equal(1, mediaHandler.Disposals);
                }
            }
            Assert.Equal(1, body.Disposals);
            Assert.Equal(1, downloadHandler.DownloadResponse.Disposals);
            Assert.Equal(2, handler.Polls);
            Assert.Equal(1, downloadHandler.Downloads);
            Assert.All(factory.Clients, client => Assert.Equal(TimeSpan.FromSeconds(100), client.Timeout));
            if (sdkOwned) Assert.Empty(factory.Clients);
        }
        finally
        {
            body.ReleaseRead.TrySetResult();
            Exception? facadeCleanup = await Record.ExceptionAsync(() => video.DisposeAsync().AsTask());
            Exception? coreCleanup = await Record.ExceptionAsync(() => core.DisposeAsync().AsTask());
            Exception? scopeCleanup = await Record.ExceptionAsync(() => scope.DisposeAsync().AsTask());
            if (generationRetirement is not null)
                await Record.ExceptionAsync(() => generationRetirement.WaitAsync(TimeSpan.FromSeconds(5)));
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
        public int Disposals { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
        }
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

    private sealed class Factory(HttpMessageHandler handler, string? defaultSecret = null) : IHttpClientFactory, IDisposable
    {
        public List<HttpClient> Clients { get; } = [];
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            if (defaultSecret is not null) client.DefaultRequestHeaders.Add("X-Default", defaultSecret);
            Clients.Add(client);
            return client;
        }
        public void Dispose()
        {
            foreach (HttpClient client in Clients) client.Dispose();
        }
    }

    private sealed class RejectedVideoHandler(bool sendFailure, string? successBody = null) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            Assert.Equal("/v1/videos/generations", request.RequestUri!.AbsolutePath);
            Assert.Equal("video-header-secret", Assert.Single(request.Headers.GetValues("X-Default")));
            if (successBody is not null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(successBody) });
            if (sendFailure)
                throw new LlmProviderException("Video refused video-test-token video-header-secret.", LlmTransport.XaiResponses,
                    HttpStatusCode.TooManyRequests, "rate_limit_video-test-token_video-header-secret",
                    "video-request-video-test-token-video-header-secret", TimeSpan.FromSeconds(17));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"error":{"code":"rate_limit_video-test-token_video-header-secret","message":"Video refused video-test-token video-header-secret."}}""")
            };
            response.Headers.Add("x-request-id", "video-request-video-test-token-video-header-secret");
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(17));
            return Task.FromResult(response);
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
