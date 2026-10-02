using System.Diagnostics;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Logging;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal sealed class SdkHttpMessageHandlerBuilder(string name, IServiceProvider services, TimeSpan lifetime, SdkHttpConfiguration configuration) : HttpMessageHandlerBuilder
{
    public override string? Name { get; set; } = name;
    public override IServiceProvider Services => services;
    public override IList<DelegatingHandler> AdditionalHandlers { get; } = new List<DelegatingHandler>();
    private HttpMessageHandler? _primary;
    public override HttpMessageHandler PrimaryHandler { get => _primary ??= CreatePrimary(lifetime); set => _primary = value; }
    internal HttpMessageHandler? CreatedPrimary => _primary;
    internal SdkHttpConfiguration Configuration => configuration;
    internal SdkHttpLoggerDefinition[] Loggers { get; set; } = [];
    public override HttpMessageHandler Build()
    {
        var seen = new HashSet<DelegatingHandler>();
        foreach (DelegatingHandler handler in AdditionalHandlers)
            if (handler is null || handler.InnerHandler is not null || !seen.Add(handler))
                throw new InvalidOperationException("Delegating handlers must be fresh, distinct instances with no assigned inner handler.");
        return CreateHandlerPipeline(PrimaryHandler, AdditionalHandlers);
    }
    internal void BuildLoggers()
    {
        foreach (SdkHttpLoggerDefinition definition in Loggers)
        {
            var handler = new SdkHttpLoggerHandler(definition.Factory(Services));
            if (definition.Wrap) AdditionalHandlers.Insert(0, handler); else AdditionalHandlers.Add(handler);
        }
        Loggers = [];
    }
    private static HttpMessageHandler CreatePrimary(TimeSpan lifetime)
    {
        if (!SocketsHttpHandler.IsSupported) return new HttpClientHandler();
        var handler = new SocketsHttpHandler();
        if (lifetime >= TimeSpan.Zero) handler.PooledConnectionLifetime = lifetime;
        return handler;
    }
}

internal sealed class SdkHttpLoggerHandler(IHttpClientLogger logger) : DelegatingHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp(); object? context = logger.LogRequestStart(request); HttpResponseMessage? response = null;
        try { response = base.Send(request, cancellationToken); logger.LogRequestStop(context, request, response, Stopwatch.GetElapsedTime(start)); return response; }
        catch (Exception error) { logger.LogRequestFailed(context, request, response, error, Stopwatch.GetElapsedTime(start)); throw; }
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp(); var asynchronous = logger as IHttpClientAsyncLogger; HttpResponseMessage? response = null;
        object? context = asynchronous is null ? logger.LogRequestStart(request) : await asynchronous.LogRequestStartAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (asynchronous is null) logger.LogRequestStop(context, request, response, Stopwatch.GetElapsedTime(start));
            else await asynchronous.LogRequestStopAsync(context, request, response, Stopwatch.GetElapsedTime(start), cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (Exception error)
        {
            if (asynchronous is null) logger.LogRequestFailed(context, request, response, error, Stopwatch.GetElapsedTime(start));
            else await asynchronous.LogRequestFailedAsync(context, request, response, error, Stopwatch.GetElapsedTime(start), cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
