using System.Net;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HttpGenerationLoggingTests
{
    private const string Pipeline = "OpenAiResponsesLlmClient";

    [Fact]
    public async Task DefaultCategoriesAndHeaderRedactionRemainActualFrameworkPolicy()
    {
        var records = new RecordingProvider(); using ILoggerFactory logging = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(records));
        var services = new ServiceCollection(); services.AddSingleton(logging);
        List<string> protectedNames = ["Authorization"];
        services.AddLlmConnect(options => options.ConfigureLogging(Pipeline, builder => builder.RemoveAllLoggers().AddDefaultLogger().RedactLoggedHeaders(protectedNames)));
        protectedNames.Clear();
        services.AddHttpClient(Pipeline).ConfigurePrimaryHttpMessageHandler(() => new ControlledHandler());
        await using ServiceProvider root = services.BuildServiceProvider(); using HttpClient client = root.GetRequiredService<SdkHttpGenerationPool>().CreateClient(Pipeline);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://controlled.invalid/");
        request.Headers.Authorization = new("Bearer", "protected-token"); request.Headers.Add("X-Shown", "exact-visible-value");
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Contains(records.Entries, entry => entry.Category == "System.Net.Http.HttpClient." + Pipeline + ".LogicalHandler");
        Assert.Contains(records.Entries, entry => entry.Category == "System.Net.Http.HttpClient." + Pipeline + ".ClientHandler");
        Assert.DoesNotContain(records.Entries, entry => entry.Message.Contains("protected-token", StringComparison.Ordinal));
        Assert.Contains(records.Entries, entry => entry.Message.Contains("exact-visible-value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GenericLoggerUsesActualScopeAndSyncAsyncSuccessFailureContracts(bool synchronous, bool fail)
    {
        var services = new ServiceCollection(); services.AddScoped<RecordingLogger>();
        services.AddLlmConnect(options => options.ConfigureLogging(Pipeline, builder => builder.RemoveAllLoggers().AddLogger<RecordingLogger>()));
        var failure = new InvalidOperationException("controlled send failure");
        services.AddHttpClient(Pipeline).ConfigurePrimaryHttpMessageHandler(() => new ControlledHandler(fail ? failure : null));
        RecordingLogger? logger = null;
        services.Configure<Microsoft.Extensions.Http.HttpClientFactoryOptions>(Pipeline, options => options.HttpMessageHandlerBuilderActions.Add(builder => logger = builder.Services.GetRequiredService<RecordingLogger>()));
        await using ServiceProvider root = services.BuildServiceProvider(); using HttpClient client = root.GetRequiredService<SdkHttpGenerationPool>().CreateClient(Pipeline);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://controlled.invalid/");
        if (fail)
        {
            Exception actual = synchronous ? Assert.Throws<InvalidOperationException>(() => client.Send(request, HttpCompletionOption.ResponseHeadersRead))
                : await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead));
            Assert.Same(failure, actual); Assert.Same(failure, logger!.Failure);
        }
        else
        {
            using HttpResponseMessage response = synchronous ? client.Send(request, HttpCompletionOption.ResponseHeadersRead)
                : await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
        Assert.Equal([synchronous ? "sync.start" : "async.start", (synchronous ? "sync." : "async.") + (fail ? "failed" : "stop")], logger!.Events);
        Assert.Equal(0, logger.Disposals);
        await root.DisposeAsync(); Assert.Equal(1, logger.Disposals);
    }

    private sealed class ControlledHandler(Exception? failure = null) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        { if (failure is not null) throw failure; return new(HttpStatusCode.OK) { Content = new StringContent("actual") }; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            failure is not null ? Task.FromException<HttpResponseMessage>(failure) : Task.FromResult(Send(request, cancellationToken));
    }
    private sealed class RecordingLogger : IHttpClientAsyncLogger, IDisposable
    {
        private readonly object _context = new();
        public List<string> Events { get; } = [];
        public Exception? Failure { get; private set; }
        public int Disposals { get; private set; }
        public object? LogRequestStart(HttpRequestMessage request) { Events.Add("sync.start"); return _context; }
        public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed)
        { Assert.Same(_context, context); Assert.True(elapsed >= TimeSpan.Zero); Events.Add("sync.stop"); }
        public void LogRequestFailed(object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception, TimeSpan elapsed)
        { Assert.Same(_context, context); Assert.Null(response); Failure = exception; Events.Add("sync.failed"); }
        public ValueTask<object?> LogRequestStartAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        { Events.Add("async.start"); return ValueTask.FromResult<object?>(_context); }
        public ValueTask LogRequestStopAsync(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed, CancellationToken cancellationToken = default)
        { Assert.Same(_context, context); Assert.True(elapsed >= TimeSpan.Zero); Events.Add("async.stop"); return ValueTask.CompletedTask; }
        public ValueTask LogRequestFailedAsync(object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception, TimeSpan elapsed, CancellationToken cancellationToken = default)
        { Assert.Same(_context, context); Assert.Null(response); Failure = exception; Events.Add("async.failed"); return ValueTask.CompletedTask; }
        public void Dispose() => Disposals++;
    }
    private sealed class RecordingProvider : ILoggerProvider
    {
        public List<(string Category, string Message)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLoggerAdapter(categoryName, Entries);
        public void Dispose() { }
    }
    private sealed class RecordingLoggerAdapter(string category, List<(string Category, string Message)> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => records.Add((category, formatter(state, exception)));
    }
}
