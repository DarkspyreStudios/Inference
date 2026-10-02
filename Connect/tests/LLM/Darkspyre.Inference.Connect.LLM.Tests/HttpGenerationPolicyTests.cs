using System.Diagnostics.Metrics;
using Darkspyre.Inference.Connect.LLM.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Logging;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HttpGenerationPolicyTests
{
    private const string Pipeline = "OpenAiResponsesLlmClient";

    [Fact]
    public async Task AllSixteenRegistrationsPreserveDefaultsAndSelectedMeter()
    {
        var services = new ServiceCollection();
        var factory = new MeterFactory();
        services.AddSingleton<IMeterFactory>(factory);
        var handlers = new Dictionary<string, HttpMessageHandler>(StringComparer.Ordinal);
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new CaptureFilter(builder => handlers.Add(builder.Name!, builder.PrimaryHandler)));
        services.AddLlmConnect();
        Azure.ServiceCollectionExtensions.AddLlmConnectAzure(services);
        Vertex.ServiceCollectionExtensions.AddLlmConnectVertex(services);
        TTS.ServiceCollectionExtensions.AddTtsConnect(services);
        Audio.ServiceCollectionExtensions.AddAudioConnect(services);
        Image.ServiceCollectionExtensions.AddImageConnect(services);
        await using ServiceProvider root = services.BuildServiceProvider();
        SdkHttpGenerationPool pool = root.GetRequiredService<SdkHttpGenerationPool>();
        string[] infinite = ["GeminiApiLlmClient", Pipeline, "OpenAiCompatibleChatLlmClient", "AnthropicMessagesLlmClient",
            "AzureResponsesLlmClient", "AzureChatLlmClient", "VertexLlmClient", "PocketTtsHttpProvider", "TangoFluxHttpProvider"];
        string[] noRedirect = ["GeminiApiLlmClient", "AzureResponsesLlmClient", "AzureChatLlmClient", "VertexLlmClient", "PocketTtsHttpProvider", "TangoFluxHttpProvider"];
        Assert.Equal(16, SdkHttpRegistration.Names.Length);
        foreach (string name in SdkHttpRegistration.Names)
        {
            using HttpClient client = pool.CreateClient(name);
            Assert.Equal(infinite.Contains(name, StringComparer.Ordinal) ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(100), client.Timeout);
            if (noRedirect.Contains(name, StringComparer.Ordinal))
            {
                HttpClientHandler handler = Assert.IsType<HttpClientHandler>(handlers[name]);
                Assert.False(handler.AllowAutoRedirect);
                Assert.Same(factory, handler.MeterFactory);
            }
            else
            {
                SocketsHttpHandler handler = Assert.IsType<SocketsHttpHandler>(handlers[name]);
                Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);
                Assert.Same(factory, handler.MeterFactory);
            }
        }
        await pool.DisposeAsync();
        Assert.Equal(0, factory.Disposals);
    }

    [Fact]
    public async Task NamedOverridesKeepExistingMeterAndOwnedHandlerScope()
    {
        var services = new ServiceCollection();
        var supplied = new MeterFactory(); var preset = new MeterFactory();
        services.AddSingleton<IMeterFactory>(supplied);
        services.AddScoped<ScopedMarker>();
        IServiceProvider? scoped = null;
        HttpMessageHandler? selected = null;
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new CaptureFilter(builder => { scoped = builder.Services; selected = builder.PrimaryHandler; }));
        services.AddLlmConnect();
        services.AddHttpClient(Pipeline).SetHandlerLifetime(TimeSpan.FromSeconds(7))
            .ConfigureHttpClient(client => client.DefaultRequestHeaders.Add("X-Controlled", "exact"))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { MeterFactory = preset, PooledConnectionLifetime = TimeSpan.FromSeconds(23) })
            .AddHttpMessageHandler(provider => new MarkerHandler(provider.GetRequiredService<ScopedMarker>()));
        await using ServiceProvider root = services.BuildServiceProvider();
        using HttpClient client = root.GetRequiredService<SdkHttpGenerationPool>().CreateClient(Pipeline);
        Assert.Equal("exact", Assert.Single(client.DefaultRequestHeaders.GetValues("X-Controlled")));
        Assert.NotSame(root, scoped);
        ScopedMarker marker = scoped!.GetRequiredService<ScopedMarker>();
        SocketsHttpHandler handler = Assert.IsType<SocketsHttpHandler>(selected);
        Assert.Same(preset, handler.MeterFactory);
        Assert.Equal(TimeSpan.FromSeconds(23), handler.PooledConnectionLifetime);
        await root.DisposeAsync();
        Assert.Equal(1, marker.Disposals);
        Assert.Equal(0, supplied.Disposals); Assert.Equal(0, preset.Disposals);
    }

    [Fact]
    public async Task LoggerFactoriesRunAfterAllBuilderActionsWithActualWrapAndFilterOrder()
    {
        var trace = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new TraceFilter("a", trace));
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new TraceFilter("b", trace));
        services.AddLlmConnect(options => options.ConfigureLogging(Pipeline, logging => logging.RemoveAllLoggers()
            .AddLogger(_ => { trace.Add("logger.outer.factory"); return new Logger("outer", trace); }, true)
            .AddLogger(_ => { trace.Add("logger.inner.factory"); return new Logger("inner", trace); })));
        services.AddHttpClient(Pipeline).ConfigurePrimaryHttpMessageHandler(() => new TerminalHandler(trace));
        services.Configure<HttpClientFactoryOptions>(Pipeline, options => options.HttpMessageHandlerBuilderActions.Add(builder =>
        {
            trace.Add("late.action");
            builder.AdditionalHandlers.Add(new TraceHandler("middleware", trace));
        }));
        await using ServiceProvider root = services.BuildServiceProvider();
        using HttpClient client = root.GetRequiredService<SdkHttpGenerationPool>().CreateClient(Pipeline);
        Assert.Equal(["a.before", "b.before", "late.action", "logger.outer.factory", "logger.inner.factory", "b.after", "a.after"], trace);
        trace.Clear();
        using HttpResponseMessage response = await client.GetAsync("https://controlled.invalid/", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(["a.send", "b.send", "outer.start", "middleware.send", "inner.start", "terminal", "inner.stop", "outer.stop"], trace);
    }

    [Fact]
    public async Task BorrowModeUsesActualHostFactoryWithoutTakingOwnership()
    {
        var services = new ServiceCollection(); var factory = new HostFactory();
        services.AddSingleton<IHttpClientFactory>(factory);
        services.AddLlmConnect(options => options.CreationMode = InferenceHttpCreationMode.BorrowHostFactory);
        await using ServiceProvider root = services.BuildServiceProvider();
        Assert.Same(factory, root.GetRequiredService<IHttpClientFactory>());
        using HttpClient client = root.GetRequiredService<SdkHttpGenerationPool>().CreateClient(Pipeline);
        Assert.Equal([Pipeline], factory.Names);
        await root.DisposeAsync();
        Assert.Equal(0, factory.Disposals);
        using HttpClient remaining = factory.CreateClient("");
        Assert.Equal(2, factory.Names.Count);
    }

    [Fact]
    public void ConfigurationRejectsUnknownNamesModesAndConflictingSelectionAndFreezesCallbacks()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentOutOfRangeException>(() => services.ConfigureInferenceHttpPipelines(options => options.CreationMode = (InferenceHttpCreationMode)9));
        Assert.Throws<ArgumentException>(() => services.ConfigureInferenceHttpPipelines(options => options.ConfigureLogging("unknown", _ => { })));
        InferenceHttpPipelineOptions? retained = null; InferenceHttpLoggingBuilder? logging = null;
        services.ConfigureInferenceHttpPipelines(options => { retained = options; options.ConfigureLogging(Pipeline, builder => logging = builder); });
        services.ConfigureInferenceHttpPipelines(_ => { });
        Assert.Throws<InvalidOperationException>(() => services.ConfigureInferenceHttpPipelines(options => options.CreationMode = InferenceHttpCreationMode.BorrowHostFactory));
        Assert.Throws<ObjectDisposedException>(() => retained!.CreationMode = InferenceHttpCreationMode.BorrowHostFactory);
        Assert.Throws<ObjectDisposedException>(() => logging!.AddDefaultLogger());
        using ServiceProvider root = services.BuildServiceProvider();
        _ = root.GetRequiredService<SdkHttpGenerationPool>();
        Assert.Throws<ObjectDisposedException>(() => services.ConfigureInferenceHttpPipelines(_ => { }));
    }

    private sealed class CaptureFilter(Action<HttpMessageHandlerBuilder> capture) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder => { next(builder); capture(builder); };
    }
    private sealed class TraceFilter(string name, List<string> trace) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            trace.Add(name + ".before"); next(builder); trace.Add(name + ".after"); builder.AdditionalHandlers.Insert(0, new TraceHandler(name, trace));
        };
    }
    private sealed class TraceHandler(string name, List<string> trace) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { trace.Add(name + ".send"); return base.SendAsync(request, cancellationToken); }
    }
    private sealed class TerminalHandler(List<string> trace) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { trace.Add("terminal"); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)); }
    }
    private sealed class Logger(string name, List<string> trace) : IHttpClientLogger
    {
        public object? LogRequestStart(HttpRequestMessage request) { trace.Add(name + ".start"); return name; }
        public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed) { Assert.Equal(name, context); trace.Add(name + ".stop"); }
        public void LogRequestFailed(object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception, TimeSpan elapsed) => throw new NotSupportedException();
    }
    private sealed class MeterFactory : IMeterFactory
    {
        public int Disposals { get; private set; }
        public Meter Create(MeterOptions options) => new(options);
        public void Dispose() => Disposals++;
    }
    private sealed class ScopedMarker : IDisposable
    {
        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
    private sealed class MarkerHandler(ScopedMarker marker) : DelegatingHandler
    {
        protected override void Dispose(bool disposing) { Assert.Equal(0, marker.Disposals); base.Dispose(disposing); }
    }
    private sealed class HostFactory : IHttpClientFactory, IDisposable
    {
        public List<string> Names { get; } = [];
        public int Disposals { get; private set; }
        public HttpClient CreateClient(string name) { Names.Add(name); return new(); }
        public void Dispose() => Disposals++;
    }
}
