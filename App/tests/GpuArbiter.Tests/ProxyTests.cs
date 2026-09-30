using System.Net;
using System.Text;
using GpuArbiter.Gpu;
using GpuArbiter.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GpuArbiter.Tests;

public sealed class ProxyTests
{
    [Fact]
    public async Task FastAdmissionPreservesUpstreamStatusHeadersAndBody()
    {
        using var rig = new Rig();
        using var lease = await rig.Admit("qwen");
        using var harness = new ProxyHarness(rig, HttpStatusCode.Created, "application/json", "{\"answer\":1}");
        using var response = await harness.Send(stream: false);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("{\"answer\":1}", await response.Content.ReadAsStringAsync());
        Assert.Contains("qwen", harness.UpstreamBody);
        Assert.Equal(1, rig.Gpu.Snapshot().LlmInFlight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingStreamSendsCommentsThenUpstreamOrErrorEvent(bool upstreamError)
    {
        using var rig = new Rig();
        using var comfy = await rig.Admit(null);
        using var harness = new ProxyHarness(rig, upstreamError ? HttpStatusCode.BadRequest : HttpStatusCode.OK,
            upstreamError ? "application/json" : "text/event-stream", upstreamError ? "{\"error\":\"bad\"}" : "data: {\"answer\":1}\n\ndata: [DONE]\n\n");
        var pending = harness.Send(stream: true);
        await WaitFor(() => rig.Gpu.Snapshot().Waiters.Count == 1);
        await WaitFor(() => rig.Clock.TimersCreated >= 1);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.StartsWith(": waiting for GPU:", await reader.ReadLineAsync());
        Assert.Equal("", await reader.ReadLineAsync());
        await WaitFor(() => rig.Clock.TimersCreated >= 2);
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.StartsWith(": waiting for GPU:", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("", await reader.ReadLineAsync());
        comfy.Dispose();
        await rig.Gpu.StepAsync(CancellationToken.None);
        var tail = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (upstreamError) Assert.Contains("\"code\":400", tail);
        else Assert.Equal("data: {\"answer\":1}\n\ndata: [DONE]\n\n", tail);
        Assert.Equal(0, rig.Gpu.Snapshot().LlmInFlight);
    }

    [Fact]
    public async Task NonStreamingWaitDoesNotStartResponseBeforeAdmission()
    {
        using var rig = new Rig();
        using var comfy = await rig.Admit(null);
        using var harness = new ProxyHarness(rig, HttpStatusCode.OK, "application/json", "{}");
        var pending = harness.Send(stream: false);
        await WaitFor(() => rig.Gpu.Snapshot().Waiters.Count == 1);
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, harness.UpstreamCalls);
        comfy.Dispose();
        await rig.Gpu.StepAsync(CancellationToken.None);
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DisconnectRemovesWaiterWithoutCallingUpstream()
    {
        using var rig = new Rig();
        using var comfy = await rig.Admit(null);
        using var harness = new ProxyHarness(rig, HttpStatusCode.OK, "application/json", "{}");
        using var cancellation = new CancellationTokenSource();
        var pending = harness.Send(stream: false, cancellation.Token);
        await WaitFor(() => rig.Gpu.Snapshot().Waiters.Count == 1);
        cancellation.Cancel();
        try { using var response = await pending; }
        catch (OperationCanceledException) { }
        await WaitFor(() => rig.Gpu.Snapshot().Waiters.Count == 0);
        Assert.Equal(0, harness.UpstreamCalls);
    }

    [Fact]
    public async Task InvalidModelCannotBecomeAnSseCommentOrLogLine()
    {
        using var rig = new Rig();
        using var harness = new ProxyHarness(rig, HttpStatusCode.OK, "application/json", "{}");
        using var response = await harness.Client.PostAsync("/v1/chat/completions",
            new StringContent("{\"model\":\"qwen\\nspoof\",\"stream\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(rig.Gpu.Snapshot().Waiters);
        Assert.Equal(0, harness.UpstreamCalls);
    }

    [Fact]
    public async Task RouterExitReturns502AndCancelsActiveForward()
    {
        using var rig = new Rig();
        using var initial = await rig.Admit("qwen");
        using var harness = new ProxyHarness(rig, HttpStatusCode.OK, "application/json", "{}") { BlockUpstream = true };
        var pending = harness.Send(stream: false);
        await WaitFor(() => harness.UpstreamCalls == 1);
        Assert.Equal(2, rig.Gpu.Snapshot().LlmInFlight);
        rig.Gpu.RouterExited(12);
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("llama-server restarted", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, rig.Gpu.Snapshot().LlmInFlight);
    }

    private static async Task WaitFor(Func<bool> predicate)
    {
        for (var i = 0; i < 5000 && !predicate(); i++) await Task.Delay(1);
        Assert.True(predicate());
    }

    private sealed class ProxyHarness : IHttpClientFactory, IDisposable
    {
        private readonly IHost _host;
        private readonly Handler _handler;
        public HttpClient Client { get; }
        public bool BlockUpstream { get; set; }
        public int UpstreamCalls { get; private set; }
        public string? UpstreamBody { get; private set; }
        public ProxyHarness(Rig rig, HttpStatusCode status, string type, string body)
        {
            _handler = new Handler(async (request, token) =>
            {
                UpstreamCalls++;
                UpstreamBody = await request.Content!.ReadAsStringAsync(token);
                if (BlockUpstream) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new(status) { Content = new StringContent(body, Encoding.UTF8, type) };
            });
            _host = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddRouting()).Configure(app =>
            {
                var proxy = new LlmProxy(rig.Gpu, this, rig.Options, rig.Clock);
                app.Run(proxy.HandleAsync);
            })).Start();
            Client = _host.GetTestClient();
        }
        public Task<HttpResponseMessage> Send(bool stream, CancellationToken token = default) => Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            { Content = new StringContent($"{{\"model\":\"qwen\",\"stream\":{stream.ToString().ToLowerInvariant()}}}", Encoding.UTF8, "application/json") },
            HttpCompletionOption.ResponseHeadersRead, token);
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
        public void Dispose() { Client.Dispose(); _host.Dispose(); _handler.Dispose(); }
        private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
        }
    }
}
