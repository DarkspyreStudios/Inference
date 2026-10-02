using System.Net;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Logging;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HttpLoggerFailureTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public async Task StopFailureReleasesUnhandedResponseOrRetainsExactRefusedCleanup(bool synchronous, bool failCleanup, bool failFailedCallback)
    {
        var original = new InvalidOperationException("controlled logger stop failure");
        var cleanup = new IOException("controlled response cleanup failure");
        var callback = new ArgumentException("controlled failed logger callback failure");
        var content = new ControlledContent(failCleanup ? cleanup : null);
        var logger = new StopLogger(original, failFailedCallback ? callback : null);
        var services = new ServiceCollection();
        services.AddLlmConnect(options => options.ConfigureLogging("OpenAiResponsesLlmClient", logging => logging.RemoveAllLoggers().AddLogger(_ => logger)));
        services.AddHttpClient("OpenAiResponsesLlmClient").ConfigurePrimaryHttpMessageHandler(() => new Handler(content));
        ServiceProvider root = services.BuildServiceProvider();
        SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        using HttpClient client = pool.CreateClient("OpenAiResponsesLlmClient");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://controlled.invalid/");
        try
        {
            Exception actual = synchronous ? Assert.ThrowsAny<Exception>(() => client.Send(request, HttpCompletionOption.ResponseHeadersRead))
                : await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead));
            Assert.Same(original, logger.Observed);
            Assert.NotNull(logger.Response);
            Assert.Equal(1, content.Disposals);
            if (failCleanup || failFailedCallback)
            {
                Exception[] expected = failFailedCallback ? failCleanup ? [original, callback, cleanup] : [original, callback] : [original, cleanup];
                Assert.Equal(expected, Assert.IsType<AggregateException>(actual).InnerExceptions);
            }
            else Assert.Same(original, actual);
            if (failCleanup)
            {
                Exception first = await Assert.ThrowsAsync<IOException>(() => pool.DisposeAsync().AsTask());
                Assert.Same(cleanup, first);
                Assert.Same(first, await Assert.ThrowsAsync<IOException>(() => pool.DisposeAsync().AsTask()));
                Assert.Equal(1, content.Disposals);
            }
            else await pool.DisposeAsync();
        }
        finally
        {
            client.Dispose();
            try { await root.DisposeAsync(); }
            catch (IOException error) when (ReferenceEquals(error, cleanup)) { }
        }
    }

    private sealed class Handler(HttpContent content) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => new(HttpStatusCode.OK) { Content = content };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Send(request, cancellationToken));
    }
    private sealed class ControlledContent(Exception? failure) : ByteArrayContent("actual"u8.ToArray())
    {
        public int Disposals { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposals++; if (failure is not null) throw failure; }
            base.Dispose(disposing);
        }
    }
    private sealed class StopLogger(Exception failure, Exception? failedCallback) : IHttpClientAsyncLogger
    {
        public Exception? Observed { get; private set; }
        public HttpResponseMessage? Response { get; private set; }
        public object? LogRequestStart(HttpRequestMessage request) => null;
        public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed) => throw failure;
        public void LogRequestFailed(object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception, TimeSpan elapsed)
        { Observed = exception; Response = response; if (failedCallback is not null) throw failedCallback; }
        public ValueTask<object?> LogRequestStartAsync(HttpRequestMessage request, CancellationToken cancellationToken = default) => ValueTask.FromResult<object?>(null);
        public ValueTask LogRequestStopAsync(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed, CancellationToken cancellationToken = default) => ValueTask.FromException(failure);
        public ValueTask LogRequestFailedAsync(object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception, TimeSpan elapsed, CancellationToken cancellationToken = default)
        { LogRequestFailed(context, request, response, exception, elapsed); return ValueTask.CompletedTask; }
    }
}
