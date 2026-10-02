using GpuArbiter;
using GpuArbiter.Backends;
using GpuArbiter.Gpu;
using GpuArbiter.Logging;
using GpuArbiter.Proxy;
using GpuArbiter.Splash;
using Yarp.ReverseProxy.Forwarder;

if (!OperatingSystem.IsMacOS() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.Arm64)
    throw new PlatformNotSupportedException("gpu-arbiter-splash requires Apple Silicon macOS.");

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
var options = builder.Configuration.GetSection("Splash").Get<SplashOptions>() ?? new();
options.Validate();
builder.WebHost.UseUrls(options.InferenceUrl, options.ComfyProxyUrl);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = options.MaximumRequestBytes);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<CoordinatorOptions>(options);
builder.Services.AddSingleton(TimeProvider.System);
var files = new FileLoggerProvider(options.LogDir);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(files);
builder.Logging.AddConsole();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.None);
builder.Logging.AddFilter("Yarp", LogLevel.None);
builder.Services.AddSingleton(files);
builder.Services.AddHttpClient("splash-control", client =>
{ client.Timeout = TimeSpan.FromSeconds(2); client.MaxResponseContentBufferSize = 2 * 1024 * 1024; })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient("llm", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddHttpClient<IComfyClient, ComfyClient>(client => { client.BaseAddress = new(options.Comfy.Url); client.Timeout = TimeSpan.FromSeconds(5); });
builder.Services.AddSingleton<ISplashControl, SplashControl>();
builder.Services.AddSingleton<ISplashProcessFactory, MacProcessFactory>();
builder.Services.AddSingleton<SplashModelHost>();
builder.Services.AddSingleton<IModelHost>(services => services.GetRequiredService<SplashModelHost>());
builder.Services.AddSingleton<IGpuReleaseGuard, MacReleaseGuard>();
builder.Services.AddSingleton<GpuCoordinator>();
builder.Services.AddHostedService(services => services.GetRequiredService<SplashModelHost>());
builder.Services.AddHostedService(services => services.GetRequiredService<GpuCoordinator>());
builder.Services.AddHostedService<ComfyWatcher>();
builder.Services.AddSingleton<InferenceForwarder>();
builder.Services.AddSingleton<SplashApi>();
builder.Services.AddSingleton<ComfyGate>();
builder.Services.AddHttpForwarder();
var app = builder.Build();
app.UseWebSockets();
var models = app.Services.GetRequiredService<SplashModelHost>();
var coordinator = app.Services.GetRequiredService<GpuCoordinator>();
models.Available += coordinator.ModelHostReady;
models.UnexpectedExit += coordinator.ModelHostExited;
using var proxyClient = new HttpMessageInvoker(new SocketsHttpHandler
{
    UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None, UseCookies = false
});
var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
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
        else await app.Services.GetRequiredService<SplashApi>().HandleAsync(context).ConfigureAwait(false);
    }
    catch (GpuException error) { await InferenceForwarder.ErrorAsync(context, error.StatusCode, error.Message).ConfigureAwait(false); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
});
await app.RunAsync().ConfigureAwait(false);
