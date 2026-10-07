using System.Net;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ProviderStreamFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredHttpGenerationDrainsHeldBodyBeforeHandlerReleaseAndRemembersCleanupFailure(bool refuseCleanup)
    {
        using var body = new HeldBody(refuseCleanup);
        using var handler = new ResponseHandler(body);
        var services = new ServiceCollection();
        services.AddLlmConnect();
        services.AddHttpClient("XaiMediaDownloadClient").ConfigurePrimaryHttpMessageHandler(() => handler);
        ServiceProvider container = services.BuildServiceProvider();
        SdkHttpGenerationPool pool = container.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient("XaiMediaDownloadClient");
        Assert.NotSame(pool, container.GetRequiredService<IHttpClientFactory>());
        using HttpResponseMessage response = await client.GetAsync("https://media.example.test/video", HttpCompletionOption.ResponseHeadersRead);
        try
        {
            Stream stream = await SdkHttpConsumption.ReadAsStreamAsync(response.Content);
            Task<int> read = stream.ReadAsync(new byte[1]).AsTask();
            await body.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task retirement = pool.DisposeAsync().AsTask();
            Task repeated = pool.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            Assert.False(repeated.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => pool.CreateClient("XaiMediaDownloadClient"));
            Assert.Equal(0, handler.Disposals);
            Exception? responseCleanup = Record.Exception(response.Dispose);
            if (refuseCleanup) Assert.Same(body.Failure, responseCleanup);
            else Assert.Null(responseCleanup);
            Assert.Equal(1, body.Disposals);
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, handler.Disposals);
            body.ReleaseRead.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
            if (refuseCleanup)
            {
                Assert.Same(body.Failure, await Assert.ThrowsAsync<InvalidOperationException>(() => retirement.WaitAsync(TimeSpan.FromSeconds(5))));
                Assert.Same(body.Failure, await Assert.ThrowsAsync<InvalidOperationException>(() => repeated.WaitAsync(TimeSpan.FromSeconds(5))));
                Assert.Same(body.Failure, await Assert.ThrowsAsync<InvalidOperationException>(() => pool.DisposeAsync().AsTask()));
                Assert.Equal(0, handler.Disposals);
            }
            else
            {
                await retirement.WaitAsync(TimeSpan.FromSeconds(5));
                await repeated.WaitAsync(TimeSpan.FromSeconds(5));
                await pool.DisposeAsync();
                Assert.Equal(1, handler.Disposals);
            }
            Assert.Equal(1, body.Disposals);
        }
        finally
        {
            body.ReleaseRead.TrySetResult();
            _ = Record.Exception(response.Dispose);
            Exception? poolCleanup = await Record.ExceptionAsync(() => pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Exception? containerCleanup = await Record.ExceptionAsync(() => container.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            if (refuseCleanup)
            {
                Assert.Same(body.Failure, poolCleanup);
                Assert.Same(body.Failure, containerCleanup);
            }
            else
            {
                Assert.Null(poolCleanup);
                Assert.Null(containerCleanup);
            }
        }
    }

    private sealed class ResponseHandler(Stream body) : HttpMessageHandler
    {
        public int Disposals { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
        }
    }

    private sealed class HeldBody(bool refuseCleanup) : Stream
    {
        private int _closed;
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception Failure { get; } = new InvalidOperationException("The controlled HTTP body refuses cleanup.");
        public int Disposals { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadEntered.TrySetResult();
            await ReleaseRead.Task;
            buffer.Span[0] = 42;
            return 1;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Disposals++;
                if (refuseCleanup) throw Failure;
            }
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, InferenceFailureCode.AuthenticationRequired)]
    [InlineData(HttpStatusCode.Forbidden, InferenceFailureCode.AccessDenied)]
    public async Task CommonFacadePreservesAuthenticationAndAccessFailureKinds(HttpStatusCode status, InferenceFailureCode expected)
    {
        var providerFailure = new LlmProviderException("The provider requires authentication or access.", LlmTransport.OpenAiResponses,
            statusCode: status, providerCode: "provider-auth-code", requestId: "request-1");
        await using var service = new LlmProviderService(new Source(), [new FailingClient(new FailingStream(providerFailure))]);
        InferenceException failure = await Assert.ThrowsAsync<InferenceException>(async () =>
        {
            await foreach (LlmStreamEvent unused in service.Bind("connection").StreamAsync(new() { ModelId = "model" }))
                throw new InvalidOperationException("The rejected request must not emit an event.");
        });
        Assert.Equal(expected, failure.Code);
        Assert.Equal(providerFailure.Message, failure.Message);
        Assert.Same(providerFailure, failure.InnerException);
    }

    [Fact]
    public async Task CleanupFailureFromFinalProviderPullIsAlsoReportedByRetirement()
    {
        var providerFailure = new LlmProviderException("The provider rejected generation.", LlmTransport.OpenAiResponses);
        Exception cleanupFailure = LlmResourcePreparation.AsCleanupFailure(new InvalidOperationException("The response could not be released."));
        var stream = new FailingStream(providerFailure, cleanupFailure, cleanupDuringPull: true);
        var service = new LlmProviderService(new Source(), [new FailingClient(stream)]);

        Exception? operation = await Record.ExceptionAsync(async () =>
        {
            await foreach (LlmStreamEvent unused in service.StreamAsync("connection", new() { ModelId = "model" }))
                throw new InvalidOperationException("The failed provider must not emit an event.");
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Exception? retirement = await Record.ExceptionAsync(async () => await service.DisposeAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Exception[] causes = Assert.IsType<AggregateException>(operation).Flatten().InnerExceptions.ToArray();
        Assert.Equal(2, causes.Length);
        Assert.Same(providerFailure, causes[0]);
        Assert.Same(cleanupFailure, causes[1]);
        Assert.Same(cleanupFailure, retirement);
        Assert.Same(retirement, await Record.ExceptionAsync(async () => await service.DisposeAsync()));
    }

    [Fact]
    public async Task ProviderFailureSurvivesFailedEnumerationCleanupAndRetirementReportsThatCleanup()
    {
        var providerFailure = new LlmProviderException("The provider rejected generation.", LlmTransport.OpenAiResponses);
        var cleanupFailure = new InvalidOperationException("The provider stream could not be released.");
        var service = new LlmProviderService(new Source(), [new FailingClient(new FailingStream(providerFailure, cleanupFailure))]);

        Exception? operation = await Record.ExceptionAsync(async () =>
        {
            await foreach (LlmStreamEvent unused in service.StreamAsync("connection", new() { ModelId = "model" }))
                throw new InvalidOperationException("The failed provider must not emit an event.");
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Exception? retirement = await Record.ExceptionAsync(async () => await service.DisposeAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));

        AggregateException failure = Assert.IsType<AggregateException>(operation);
        Exception[] causes = failure.Flatten().InnerExceptions.ToArray();
        Assert.Equal(2, causes.Length);
        Assert.Same(providerFailure, causes[0]);
        var cleanup = Assert.IsType<InferenceResourceOperationException>(causes[1]);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
        Assert.Same(cleanupFailure, cleanup.InnerException);
        Assert.Same(cleanup, retirement);
        Assert.Same(retirement, await Record.ExceptionAsync(async () => await service.DisposeAsync()));
    }

    [Fact]
    public async Task AbandoningAStreamReportsCleanupThatFailedDuringTheFinalProviderPull()
    {
        var cleanupFailure = new InvalidOperationException("The provider stream could not be released.");
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new LlmProviderService(new Source(), [new FailingClient(new AbandoningStream(cleanupFailure, reading))]);
        IAsyncEnumerator<LlmStreamEvent> events = service.StreamAsync("connection", new() { ModelId = "model" }).GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Exception? abandonment = await Record.ExceptionAsync(async () => await events.DisposeAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));
        Exception? retirement = await Record.ExceptionAsync(async () => await service.DisposeAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Exception[] causes = Assert.IsType<AggregateException>(abandonment).Flatten().InnerExceptions.ToArray();
        Assert.Equal(2, causes.Length);
        Assert.IsAssignableFrom<OperationCanceledException>(causes[0]);
        var cleanup = Assert.IsType<InferenceResourceOperationException>(causes[1]);
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase);
        Assert.Same(cleanupFailure, cleanup.InnerException);
        Assert.Same(cleanup, retirement);
        Assert.Same(retirement, await Record.ExceptionAsync(async () => await service.DisposeAsync()));
    }

    private sealed class Source : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LlmConnection?>(new()
            {
                Version = "1",
                Transport = LlmTransport.OpenAiResponses,
                Endpoint = "https://provider.example.test/v1",
                Credential = new LlmCredential { ApiKey = "test-key" }.Serialize()
            });

        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FailingClient(IAsyncEnumerable<LlmStreamEvent> stream) : ILlmClient
    {
        public LlmTransport Transport => LlmTransport.OpenAiResponses;
        public LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;
        public LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null) => new();
        public Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LlmModel>>([]);
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmConnection connection, LlmChatRequest request,
            CancellationToken cancellationToken = default) => stream;
    }

    private sealed class AbandoningStream(Exception cleanupFailure, TaskCompletionSource reading)
        : IAsyncEnumerable<LlmStreamEvent>, IAsyncEnumerator<LlmStreamEvent>
    {
        private CancellationToken _cancellationToken;
        private bool _started;
        public LlmStreamEvent Current => new LlmTextDeltaEvent("partial");
        public IAsyncEnumerator<LlmStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _cancellationToken = cancellationToken;
            return this;
        }
        public async ValueTask<bool> MoveNextAsync()
        {
            if (!_started) { _started = true; return true; }
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, _cancellationToken);
            return false;
        }
        public ValueTask DisposeAsync() => ValueTask.FromException(cleanupFailure);
    }

    private sealed class FailingStream(Exception providerFailure, Exception? cleanupFailure = null, bool cleanupDuringPull = false)
        : IAsyncEnumerable<LlmStreamEvent>, IAsyncEnumerator<LlmStreamEvent>
    {
        public LlmStreamEvent Current => throw new InvalidOperationException("No event is available.");
        public IAsyncEnumerator<LlmStreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromException<bool>(cleanupDuringPull && cleanupFailure is not null
            ? new AggregateException(providerFailure, cleanupFailure) : providerFailure);
        public ValueTask DisposeAsync() => cleanupDuringPull || cleanupFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(cleanupFailure);
    }
}
