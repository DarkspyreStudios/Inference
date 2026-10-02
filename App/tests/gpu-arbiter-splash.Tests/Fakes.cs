using System.Net;
using System.Text;
using GpuArbiter.Backends;
using GpuArbiter.Gpu;
using GpuArbiter.Proxy;
using GpuArbiter.Splash;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GpuArbiter.Splash.Tests;

internal sealed class FakeProcess : ISplashProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Pid => 777;
    public Task<int> Exit => _exit.Task;
    public bool GroupAlive { get; set; } = true;
    public bool SurviveStop { get; set; }
    public int Stops { get; private set; }
    public void Crash(int code = 17) => _exit.TrySetResult(code);
    public Task StopAsync(TimeSpan timeout, CancellationToken token)
    {
        Stops++;
        if (!SurviveStop) { GroupAlive = false; _exit.TrySetResult(0); }
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeProcesses : ISplashProcessFactory
{
    public List<FakeProcess> Started { get; } = [];
    public FakeProcess? Next { get; set; }
    public ISplashProcess Start(SplashOptions options, SplashModelOptions model)
    {
        var process = Next ?? new();
        Next = null;
        Started.Add(process);
        return process;
    }
}

internal sealed class FakeControl : ISplashControl
{
    public Task<bool> ReadyAsync(CancellationToken token) => Task.FromResult(Observation.Ready);
    public SplashObservation Observation { get; set; } = new(true, false, true, true, "normal", 123456, 234567, true, 777);
    public int Reads { get; private set; }
    public Task<SplashObservation> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Reads++;
        return Task.FromResult(Observation);
    }
}

internal sealed class FakeComfy : IComfyClient
{
    public ComfyState State { get; set; } = new(false, 0, 0);
    public int Frees { get; private set; }
    public Task<ComfyState> QueueAsync(CancellationToken token) => Task.FromResult(State);
    public Task FreeAsync(CancellationToken token) { Frees++; return Task.CompletedTask; }
}

internal sealed class ApiRig : IHttpClientFactory, IAsyncDisposable
{
    public SplashOptions Options { get; } = new() { Models = [new(), new() { Id = "local/other" }], CleanupTimeoutSeconds = 0.2 };
    public FakeProcesses Processes { get; } = new();
    public FakeControl Control { get; } = new();
    public FakeComfy Comfy { get; } = new();
    public SplashModelHost Models { get; }
    public GpuCoordinator Coordinator { get; }
    private readonly IHost _web;
    private readonly Handler _handler;
    public HttpClient Client { get; }
    public string? Body { get; private set; }
    public Uri? Upstream { get; private set; }
    public string Method { get; private set; } = "";
    public Dictionary<string, string> Headers { get; private set; } = [];
    public int Calls { get; private set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string Result { get; set; } = "{\"id\":\"resp_abc\"}";
    public string ContentType { get; set; } = "application/json";
    public Func<CancellationToken, Task>? OnSend { get; set; }

    public ApiRig()
    {
        Models = new(Options, Processes, Control, TimeProvider.System, NullLogger<SplashModelHost>.Instance);
        Coordinator = new(Models, Comfy, new MacReleaseGuard(Models), Options, TimeProvider.System, NullLogger<GpuCoordinator>.Instance);
        Models.Available += Coordinator.ModelHostReady;
        Models.UnexpectedExit += Coordinator.ModelHostExited;
        _handler = new Handler(async (request, token) =>
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            Upstream = request.RequestUri;
            Method = request.Method.Method;
            Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(',', h.Value), StringComparer.OrdinalIgnoreCase);
            Calls++;
            if (OnSend is not null) await OnSend(token);
            return new(Status) { Content = new StringContent(Result, Encoding.UTF8, ContentType) };
        });
        var api = new SplashApi(Options, Models, Coordinator, new(this), TimeProvider.System);
        _web = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddRouting())
            .Configure(app => app.Run(api.HandleAsync))).Start();
        Client = _web.GetTestClient();
    }
    public async Task StartAsync() { await Models.StartAsync(CancellationToken.None); await Coordinator.StartAsync(CancellationToken.None); }
    public Task<HttpResponseMessage> Post(string body, string route = "/v1/chat/completions", CancellationToken token = default) =>
        Client.PostAsync(route, new StringContent(body, Encoding.UTF8, "application/json"), token);
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    public async ValueTask DisposeAsync()
    {
        foreach (var process in Processes.Started) process.SurviveStop = false;
        await Coordinator.StopAsync(CancellationToken.None);
        await Models.StopAsync(CancellationToken.None);
        Client.Dispose(); _web.Dispose(); _handler.Dispose(); Coordinator.Dispose();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    public static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
}
