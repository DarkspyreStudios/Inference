using System.Net;
using GpuArbiter.Gpu;
using Xunit;

namespace GpuArbiter.Splash.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task DiscoveryDoesNotStartModelAndGpuNeverFabricatesGlobalVram()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        Assert.Contains("incoai/Qwen3.8-27B-Splash", await rig.Client.GetStringAsync("/v1/models"));
        var gpu = await rig.Client.GetStringAsync("/gpu");
        Assert.Contains("\"vramUsedMiB\":null", gpu);
        Assert.Contains("owned-process-termination", gpu);
        Assert.Empty(rig.Processes.Started);
        Assert.Equal(0, rig.Calls);
    }

    [Theory]
    [InlineData("{}", HttpStatusCode.BadRequest)]
    [InlineData("{\"model\":\"unknown\"}", HttpStatusCode.NotFound)]
    [InlineData("{\"model\":\"incoai/Qwen3.8-27B-Splash\",\"stream\":\"yes\"}", HttpStatusCode.BadRequest)]
    [InlineData("{\"model\":\"qwen\\nspoof\"}", HttpStatusCode.BadRequest)]
    public async Task InvalidInputFailsBeforeAdmission(string body, HttpStatusCode expected)
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        using var response = await rig.Post(body);
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(rig.Processes.Started);
        Assert.Equal(0, rig.Calls);
    }

    [Fact]
    public async Task OpaqueToolsImagesAndFormatsPassUnchangedAndCredentialsStayOutside()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        const string body = "{\"model\":\"incoai/Qwen3.8-27B-Splash\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,abc\"}}]}],\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"weather\"}}],\"response_format\":{\"type\":\"json_schema\"}}";
        rig.Client.DefaultRequestHeaders.Add("Authorization", "Bearer test-secret");
        rig.Client.DefaultRequestHeaders.Add("X-Api-Key", "test-secret");
        rig.Client.DefaultRequestHeaders.Add("X-Credential", "test-secret");
        rig.Client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        rig.Status = HttpStatusCode.Created;
        using var response = await rig.Post(body, "/v1/chat/completions?test=1");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(body, rig.Body);
        Assert.Equal("http://127.0.0.1:8085/v1/chat/completions?test=1", rig.Upstream!.ToString());
        Assert.False(rig.Headers.ContainsKey("Authorization"));
        Assert.False(rig.Headers.ContainsKey("X-Api-Key"));
        Assert.False(rig.Headers.ContainsKey("X-Credential"));
        Assert.Equal("2023-06-01", rig.Headers["anthropic-version"]);
        Assert.Equal(rig.Result, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StreamingBytesPassThroughAndLeaseEndsAfterNativeIdle()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        rig.ContentType = "text/event-stream"; rig.Result = "data: {\"choices\":[]}\n\ndata: [DONE]\n\n";
        using var response = await rig.Post("{\"model\":\"incoai/Qwen3.8-27B-Splash\",\"stream\":true}");
        Assert.Equal(rig.Result, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, rig.Coordinator.Snapshot().LlmInFlight);
    }

    [Fact]
    public async Task StoredResponseReadDeletePinResidentAndDoNotReload()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        using (var missing = await rig.Client.GetAsync("/v1/responses/resp_abc")) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(rig.Processes.Started);
        using (var created = await rig.Post("{\"model\":\"incoai/Qwen3.8-27B-Splash\",\"input\":\"hi\",\"store\":true}", "/v1/responses")) Assert.True(created.IsSuccessStatusCode);
        using (var read = await rig.Client.GetAsync("/v1/responses/resp_abc")) Assert.True(read.IsSuccessStatusCode);
        Assert.Equal("GET", rig.Method);
        using (var deleted = await rig.Client.DeleteAsync("/v1/responses/resp_abc")) Assert.True(deleted.IsSuccessStatusCode);
        Assert.Equal("DELETE", rig.Method);
        using (var unloaded = await rig.Post("{\"model\":\"incoai/Qwen3.8-27B-Splash\"}", "/models/unload")) Assert.True(unloaded.IsSuccessStatusCode);
        using (var missing = await rig.Client.GetAsync("/v1/responses/resp_abc")) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Single(rig.Processes.Started);
    }

    [Fact]
    public async Task UnsupportedOperationsAreExplicitAndAnthropicErrorsUseNativeEnvelope()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        using var unsupported = await rig.Post("{}", "/v1/embeddings");
        Assert.Equal(HttpStatusCode.NotFound, unsupported.StatusCode);
        using var anthropic = await rig.Post("{}", "/v1/messages");
        Assert.Contains("\"type\":\"error\"", await anthropic.Content.ReadAsStringAsync());
        Assert.Empty(rig.Processes.Started);
    }

    [Fact]
    public async Task OversizedBodyAndStreamingOnJsonOnlyRouteFailBeforeLoad()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        rig.Options.MaximumRequestBytes = 5;
        using (var oversized = await rig.Post("{\"model\":\"incoai/Qwen3.8-27B-Splash\"}")) Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        rig.Options.MaximumRequestBytes = 1024;
        using (var stream = await rig.Post("{\"model\":\"incoai/Qwen3.8-27B-Splash\",\"stream\":true}", "/v1/systemone")) Assert.Equal(HttpStatusCode.BadRequest, stream.StatusCode);
        Assert.Empty(rig.Processes.Started);
    }

    [Fact]
    public async Task DisconnectRetainsLeaseUntilNativeCancellationIsConfirmed()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        rig.Control.Observation = rig.Control.Observation with { Idle = false };
        rig.OnSend = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        using var cancellation = new CancellationTokenSource();
        var request = rig.Post("{\"model\":\"incoai/Qwen3.8-27B-Splash\"}", token: cancellation.Token);
        await ApiRig.WaitFor(() => rig.Calls == 1);
        cancellation.Cancel();
        Assert.Equal(1, rig.Coordinator.Snapshot().LlmInFlight);
        var unload = rig.Coordinator.UnloadAsync("incoai/Qwen3.8-27B-Splash", CancellationToken.None);
        Assert.False(unload.IsCompleted);
        rig.Control.Observation = rig.Control.Observation with { Idle = true };
        try { using var response = await request; } catch (OperationCanceledException) { }
        await unload.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rig.Processes.Started[0].GroupAlive);
    }

    [Fact]
    public async Task UnconfirmedNativeCompletionStopsGroupBeforeReleasingLease()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        rig.Control.Observation = rig.Control.Observation with { Idle = false };
        using var response = await rig.Post("{\"model\":\"incoai/Qwen3.8-27B-Splash\"}");
        Assert.False(rig.Processes.Started[0].GroupAlive);
        Assert.Equal(0, rig.Coordinator.Snapshot().LlmInFlight);
    }

    [Fact]
    public async Task ComfyHandoffConfirmsGroupExitAndReturningInferenceFreesCaches()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        rig.Comfy.State = new(true, 0, 0);
        using (var response = await rig.Post("{\"model\":\"incoai/Qwen3.8-27B-Splash\"}")) Assert.True(response.IsSuccessStatusCode);
        using (var comfy = await rig.Coordinator.AcquireAsync(GpuTarget.Comfy, CancellationToken.None))
            Assert.False(rig.Processes.Started[0].GroupAlive);
        using var returned = await rig.Post("{\"model\":\"incoai/Qwen3.8-27B-Splash\"}");
        Assert.Equal(2, rig.Comfy.Frees);
        Assert.Equal(2, rig.Processes.Started.Count);
    }
}
