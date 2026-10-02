using System.Text.Json;
using Darkspyre.Inference.Tensor.Registry;
using GpuArbiter;
using GpuArbiter.Backends;
using GpuArbiter.Gpu;
using GpuArbiter.Logging;
using GpuArbiter.Proxy;
using GpuArbiter.Tensor;
using Microsoft.AspNetCore.Http.Features;
using Yarp.ReverseProxy.Forwarder;

if (args.FirstOrDefault() == "prepare-qwen")
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
    try { return await ModelPreparation.RunAsync(args, cancellation.Token); }
    catch (OperationCanceledException) { return 130; }
    catch (Exception) { Console.Error.WriteLine("Model preparation failed. Check the local file, license, pack store and manifest destination."); return 1; }
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(service => service.ServiceName = "gpu-arbiter-tensor");
var options = builder.Configuration.GetSection("Tensor").Get<TensorOptions>() ?? new();
options.Validate();
builder.WebHost.UseUrls(options.InferenceUrl, options.ComfyProxyUrl);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = options.MaximumRequestBytes);
builder.Services.Configure<FormOptions>(form =>
{
    // Keep bounded multipart uploads in memory; the server writes no audio to system temporary files.
    form.MemoryBufferThreshold = checked((int)options.MaximumRequestBytes);
    form.MultipartBodyLengthLimit = options.MaximumRequestBytes;
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<CoordinatorOptions>(options);
builder.Services.AddSingleton(TimeProvider.System);
var files = new FileLoggerProvider(options.LogDir);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(files);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.None);
builder.Logging.AddFilter("Yarp", LogLevel.None);
builder.Services.AddSingleton(files);
builder.Services.AddTensorEngines();
builder.Services.AddSingleton<TensorModelHost>();
builder.Services.AddSingleton<ITensorModelHost>(services => services.GetRequiredService<TensorModelHost>());
builder.Services.AddSingleton<IModelHost>(services => services.GetRequiredService<TensorModelHost>());
builder.Services.AddHttpClient<IComfyClient, ComfyClient>(http => { http.BaseAddress = new(options.Comfy.Url); http.Timeout = TimeSpan.FromSeconds(5); });
builder.Services.AddSingleton<IVramProbe, VramProbe>();
builder.Services.AddSingleton<GpuCoordinator>();
builder.Services.AddHostedService(services => services.GetRequiredService<TensorModelHost>());
builder.Services.AddHostedService(services => services.GetRequiredService<GpuCoordinator>());
builder.Services.AddHostedService<ComfyWatcher>();
builder.Services.AddSingleton<AdmissionGate>();
builder.Services.AddSingleton<TextApi>();
builder.Services.AddSingleton<MediaApi>();
builder.Services.AddSingleton<ComfyGate>();
builder.Services.AddHttpForwarder();
var app = builder.Build();
app.UseWebSockets();
using var proxyClient = new HttpMessageInvoker(new SocketsHttpHandler
{ UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None, UseCookies = false });
var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
var coordinator = app.Services.GetRequiredService<GpuCoordinator>();
var models = app.Services.GetRequiredService<ITensorModelHost>();
var forwardConfiguration = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromHours(1) };
app.Run(async context =>
{
    try
    {
        if (context.Connection.LocalPort == new Uri(options.ComfyProxyUrl).Port)
        {
            async Task Forward() => await forwarder.SendAsync(context, options.Comfy.Url, proxyClient, forwardConfiguration, HttpTransformer.Default);
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/prompt")
                await app.Services.GetRequiredService<ComfyGate>().ForwardAsync(context, Forward);
            else await Forward();
            return;
        }
        if (HttpMethods.IsGet(context.Request.Method))
        {
            object WireModel(HostedModel model) => new { id = model.Alias, @object = "model", created = 0, owned_by = model.Instance switch
            { Darkspyre.Inference.Tensor.LLM.ITensorLlmInference llm => llm.Descriptor.Id,
                Darkspyre.Inference.Tensor.Image.ITensorImageInference image => image.Descriptor.Id,
                Darkspyre.Inference.Tensor.STT.ITensorSpeechToTextInference speech => speech.Descriptor.Id,
                _ => "darkspyre.tensor" } };
            if (context.Request.Path == "/gpu") await context.Response.WriteAsJsonAsync(await coordinator.SnapshotAsync(context.RequestAborted), context.RequestAborted);
            else if (context.Request.Path == "/v1/models") await context.Response.WriteAsJsonAsync(new { @object = "list", data = models.Models.Select(WireModel) }, context.RequestAborted);
            else if (context.Request.Path.Value?.StartsWith("/v1/models/", StringComparison.Ordinal) == true)
                await context.Response.WriteAsJsonAsync(WireModel(models.Get(context.Request.Path.Value[11..])), context.RequestAborted);
            else if (context.Request.Path == "/health/live") await context.Response.WriteAsJsonAsync(new { status = "live" }, context.RequestAborted);
            else throw new ApiException(404, "Unknown operation");
            return;
        }
        if (!HttpMethods.IsPost(context.Request.Method)) throw new ApiException(405, "Method is not supported");
        if (context.Request.Path is var audioPath && (audioPath == "/v1/audio/transcriptions" || audioPath == "/v1/audio/translations"))
        {
            await app.Services.GetRequiredService<MediaApi>().AudioAsync(context);
            return;
        }
        if (context.Request.Path.Value is not ("/v1/chat/completions" or "/v1/completions" or "/v1/responses" or "/v1/images/generations" or "/models/load" or "/models/unload"))
            throw new ApiException(501, "This operation has no supported Tensor capability");
        using var json = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        var root = json.RootElement;
        if (context.Request.Path is var path && (path == "/models/load" || path == "/models/unload"))
        {
            OpenAiInput.Fields(root, "model");
            var model = models.Get(OpenAiInput.String(root, "model"));
            if (path == "/models/load") { using var lease = await coordinator.AcquireAsync(GpuTarget.Llm(model.Alias), context.RequestAborted); }
            else await coordinator.UnloadAsync(model.Alias, context.RequestAborted);
            await context.Response.WriteAsJsonAsync(new { success = true }, context.RequestAborted);
        }
        else if (context.Request.Path == "/v1/images/generations") await app.Services.GetRequiredService<MediaApi>().ImagesAsync(context, root);
        else await app.Services.GetRequiredService<TextApi>().HandleAsync(context, root);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception error) { await ApiException.WriteAsync(context, error); }
});
await app.RunAsync();
return 0;
