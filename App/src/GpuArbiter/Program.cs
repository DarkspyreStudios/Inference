using System.Text.Json;
using GpuArbiter;
using GpuArbiter.Backends;
using GpuArbiter.Gpu;
using GpuArbiter.Logging;
using GpuArbiter.Proxy;
using GpuArbiter.Supervision;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(service => service.ServiceName = "gpu-arbiter");
var options = builder.Configuration.GetSection("Arbiter").Get<ArbiterOptions>() ?? new();
options.Validate();
builder.WebHost.UseUrls(options.LlmUrl, options.ComfyProxyUrl);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
var files = new FileLoggerProvider(options.LogDir);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(files);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Services.AddSingleton(files);
builder.Services.AddHttpClient<ILlamaRouterClient, LlamaRouterClient>(http =>
{ http.BaseAddress = new(options.Llama.Url); http.Timeout = TimeSpan.FromSeconds(10); });
builder.Services.AddHttpClient<IComfyClient, ComfyClient>(http =>
{ http.BaseAddress = new(options.Comfy.Url); http.Timeout = TimeSpan.FromSeconds(5); });
builder.Services.AddHttpClient("health", http => http.Timeout = TimeSpan.FromSeconds(2));
builder.Services.AddHttpClient("llm", http => http.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddSingleton<IVramProbe, VramProbe>();
builder.Services.AddSingleton<GpuCoordinator>();
builder.Services.AddHostedService(services => services.GetRequiredService<GpuCoordinator>());
builder.Services.AddHostedService<ComfyWatcher>();
builder.Services.AddHostedService<LlamaServerSupervisor>();
builder.Services.AddSingleton<LlmProxy>();
builder.Services.AddSingleton<ComfyGate>();
builder.Services.AddHttpForwarder();
var app = builder.Build();
app.UseWebSockets();
using var proxyClient = new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    UseCookies = false
});
var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
var coordinator = app.Services.GetRequiredService<GpuCoordinator>();
var requestConfig = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromHours(1) };
app.Run(async context =>
{
    try
    {
        if (context.Connection.LocalPort == new Uri(options.ComfyProxyUrl).Port)
        {
            async Task ForwardComfy() => await forwarder.SendAsync(context, options.Comfy.Url, proxyClient, requestConfig, HttpTransformer.Default).ConfigureAwait(false);
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/prompt")
                await app.Services.GetRequiredService<ComfyGate>().ForwardAsync(context, ForwardComfy).ConfigureAwait(false);
            else await ForwardComfy().ConfigureAwait(false);
        }
        else if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/gpu")
            await context.Response.WriteAsJsonAsync(await coordinator.SnapshotAsync(context.RequestAborted).ConfigureAwait(false), context.RequestAborted).ConfigureAwait(false);
        else if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path is var path && (path == "/models/load" || path == "/models/unload"))
        {
            var body = await RequestBody.ReadAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
            if (body.Model is null) throw new JsonException("Model is required.");
            if (path == "/models/load")
            {
                using var lease = await coordinator.AcquireAsync(GpuTarget.Llm(body.Model), context.RequestAborted).ConfigureAwait(false);
            }
            else await coordinator.UnloadAsync(body.Model, context.RequestAborted).ConfigureAwait(false);
            await context.Response.WriteAsJsonAsync(new { success = true }, context.RequestAborted).ConfigureAwait(false);
        }
        else if (HttpMethods.IsPost(context.Request.Method) && LlmProxy.Routes.Contains(context.Request.Path.Value!))
            await app.Services.GetRequiredService<LlmProxy>().HandleAsync(context).ConfigureAwait(false);
        else if (HttpMethods.IsGet(context.Request.Method))
            await forwarder.SendAsync(context, options.Llama.Url, proxyClient, requestConfig, HttpTransformer.Default).ConfigureAwait(false);
        else context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
    }
    catch (JsonException) { await LlmProxy.ErrorAsync(context, 400, "Invalid JSON request or model identifier").ConfigureAwait(false); }
    catch (GpuException error) { await LlmProxy.ErrorAsync(context, error.StatusCode, error.Message).ConfigureAwait(false); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
});
await app.RunAsync().ConfigureAwait(false);
