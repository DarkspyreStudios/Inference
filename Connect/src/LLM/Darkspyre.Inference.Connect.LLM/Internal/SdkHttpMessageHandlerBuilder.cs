using System.Diagnostics;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Logging;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal sealed class SdkHttpMessageHandlerBuilder(string name, IServiceProvider services, TimeSpan lifetime, SdkHttpConfiguration configuration,
    SdkHttpGenerationPool owner, SdkHttpPipelineEntry entry) : HttpMessageHandlerBuilder
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
            var handler = new SdkHttpLoggerHandler(definition.Factory(Services), owner, entry);
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

internal sealed class SdkHttpLoggerHandler(IHttpClientLogger logger, SdkHttpGenerationPool owner, SdkHttpPipelineEntry entry) : DelegatingHandler
{
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp(); object? context = logger.LogRequestStart(request); HttpResponseMessage? response = null;
        try { response = base.Send(request, cancellationToken); logger.LogRequestStop(context, request, response, Stopwatch.GetElapsedTime(start)); return response; }
        catch (Exception error)
        {
            Exception? loggingFailure = null;
            try { logger.LogRequestFailed(context, request, response, error, Stopwatch.GetElapsedTime(start)); }
            catch (Exception failure) { loggingFailure = failure; }
            Fail(error, loggingFailure, response); throw;
        }
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
            Exception? loggingFailure = null;
            try
            {
                if (asynchronous is null) logger.LogRequestFailed(context, request, response, error, Stopwatch.GetElapsedTime(start));
                else await asynchronous.LogRequestFailedAsync(context, request, response, error, Stopwatch.GetElapsedTime(start), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) { loggingFailure = failure; }
            Fail(error, loggingFailure, response); throw;
        }
    }
    private void Fail(Exception original, Exception? loggingFailure, HttpResponseMessage? response)
    {
        var errors = new List<Exception> { original };
        if (loggingFailure is not null) errors.Add(loggingFailure);
        if (response is not null)
            try { response.Dispose(); }
            catch (Exception cleanup) { owner.RecordFailure(entry, response, cleanup); errors.Add(cleanup); }
        SdkHttpGenerationPool.ThrowFailures(errors);
    }
}
