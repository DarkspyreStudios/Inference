using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Tensor.Common;
using GpuArbiter.Gpu;
using GpuArbiter.Tensor;
using Xunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace GpuArbiter.Tensor.Tests;

public sealed class ApiTests
{
    private const string Chat = "{\"model\":\"public-model\",\"messages\":[{\"role\":\"user\",\"content\":\"Hi\"}],\"max_completion_tokens\":16}";

    [Fact]
    public async Task ChatMapsAliasAndUsageAndReleasesAdmission()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        using var response = JsonDocument.Parse(await rig.SendAsync(Chat));
        Assert.Equal("public-model", response.RootElement.GetProperty("model").GetString());
        Assert.Equal("Hello world", response.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(3, response.RootElement.GetProperty("usage").GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32());
        Assert.Equal("actual-model", Assert.Single(rig.Models.Engine.Requests).ModelId);
        Assert.True(rig.Models.Engine.EnumeratorDisposed);
        Assert.Equal(0, rig.Coordinator.Snapshot().LlmInFlight);
    }

    [Theory]
    [InlineData("\"top_p\":0.9")]
    [InlineData("\"store\":true")]
    [InlineData("\"reasoning_effort\":\"medium\"")]
    [InlineData("\"tool_choice\":\"auto\"")]
    [InlineData("\"parallel_tool_calls\":false")]
    [InlineData("\"n\":9")]
    [InlineData("\"max_tokens\":8")]
    public async Task UnsupportedFieldsFailBeforeAdmission(string field)
    {
        await using var rig = new ApiRig();
        using var response = JsonDocument.Parse(await rig.SendAsync(Chat[..^1] + "," + field + "}"));
        Assert.Equal(400, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Empty(rig.Models.Engine.Requests);
        Assert.Empty(rig.Coordinator.Snapshot().Waiters);
    }

    [Fact]
    public async Task UnknownAliasReturns404WithoutLoading()
    {
        await using var rig = new ApiRig();
        using var response = JsonDocument.Parse(await rig.SendAsync(Chat.Replace("public-model", "missing")));
        Assert.Equal(404, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Empty(rig.Coordinator.Snapshot().Waiters);
    }

    [Fact]
    public async Task ChatStreamCarriesToolsUsageAndDone()
    {
        await using var rig = new ApiRig(); await rig.StartAsync(); rig.Models.Engine.EmitTools = true;
        var response = await rig.SendAsync(Chat[..^1] + ",\"stream\":true,\"stream_options\":{\"include_usage\":true}}");
        Assert.Contains("\"role\":\"assistant\"", response);
        Assert.Contains("\"id\":\"call-1\"", response);
        Assert.Contains("\"name\":\"lookup\"", response);
        Assert.Contains("\"finish_reason\":\"tool_calls\"", response);
        Assert.Contains("\"choices\":[],\"usage\":", response);
        Assert.EndsWith("data: [DONE]\n\n", response);
    }

    [Fact]
    public async Task ResponsesStreamHasOrderedLifecycleAndFinalOutput()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        var response = await rig.SendAsync("{\"model\":\"public-model\",\"input\":\"Hi\",\"stream\":true}", "/v1/responses");
        var events = response.Split('\n').Where(line => line.StartsWith("event: ")).ToArray();
        Assert.Equal("event: response.created", events[0]);
        Assert.Equal("event: response.in_progress", events[1]);
        Assert.True(Array.IndexOf(events, "event: response.output_item.added") < Array.IndexOf(events, "event: response.output_text.delta"));
        Assert.True(Array.IndexOf(events, "event: response.output_text.done") < Array.IndexOf(events, "event: response.output_item.done"));
        Assert.Equal("event: response.completed", events[^1]);
        Assert.Contains("Hello world", response);
        Assert.DoesNotContain("data: [DONE]", response);
    }

    [Fact]
    public async Task ResponsesFunctionHistoryMapsToPublicToolContracts()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        const string input = "{\"model\":\"public-model\",\"input\":[{\"type\":\"function_call\",\"call_id\":\"x\",\"name\":\"lookup\",\"arguments\":\"{}\"},{\"type\":\"function_call_output\",\"call_id\":\"x\",\"output\":\"found\"}]}";
        using var response = JsonDocument.Parse(await rig.SendAsync(input, "/v1/responses"));
        Assert.Equal("completed", response.RootElement.GetProperty("status").GetString());
        var messages = Assert.Single(rig.Models.Engine.Requests).Messages;
        Assert.Equal("x", Assert.Single(messages[0].ToolCalls).Id);
        Assert.Equal("x", Assert.Single(messages[1].ToolResults).ToolCallId);
    }

    [Fact]
    public async Task MultipleCompletionPromptsHaveDistinctStableIndices()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        using var response = JsonDocument.Parse(await rig.SendAsync("{\"model\":\"public-model\",\"prompt\":[\"One\",\"Two\"],\"n\":2}", "/v1/completions"));
        Assert.Equal(4, response.RootElement.GetProperty("choices").GetArrayLength());
        Assert.Equal([0, 1, 2, 3], response.RootElement.GetProperty("choices").EnumerateArray().Select(choice => choice.GetProperty("index").GetInt32()));
        Assert.Equal(4, rig.Models.Engine.Requests.Count);
    }

    [Fact]
    public async Task UnknownUsageRemainsNull()
    {
        await using var rig = new ApiRig(); await rig.StartAsync(); rig.Models.Engine.NoUsage = true;
        using var response = JsonDocument.Parse(await rig.SendAsync(Chat));
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("usage").ValueKind);
    }

    [Fact]
    public async Task DisconnectDisposesNativeEnumerationAndReleasesLease()
    {
        await using var rig = new ApiRig(); await rig.StartAsync(); rig.Models.Engine.HoldExecution = true;
        using var cancellation = new CancellationTokenSource();
        var context = ApiRig.Context(token: cancellation.Token);
        using var request = JsonDocument.Parse(Chat);
        var running = rig.Api.HandleAsync(context, request.RootElement);
        await rig.Models.Engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, rig.Coordinator.Snapshot().LlmInFlight);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(rig.Models.Engine.EnumeratorDisposed);
        Assert.Equal(0, rig.Coordinator.Snapshot().LlmInFlight);
    }

    [Fact]
    public async Task ErrorsDoNotExposeEnginePromptOrProviderPayload()
    {
        await using var rig = new ApiRig(); await rig.StartAsync(); rig.Models.Engine.FailExecution = true;
        var response = await rig.SendAsync(Chat);
        Assert.DoesNotContain("secret-prompt", response);
        Assert.Contains("\"code\":503", response);
        Assert.Equal(0, rig.Coordinator.Snapshot().LlmInFlight);
    }

    [Fact]
    public async Task LifecycleWaitsForCompletionInsteadOfAcceptance()
    {
        var engine = new FakeEngine();
        var waiting = TensorModelHost.AwaitStateAsync(engine, () => Task.CompletedTask, TensorEngineState.Loaded, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        engine.Transition(TensorEngineState.Loaded);
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task LifecycleFailureAndCancellationDoNotRemainSubscribed()
    {
        var engine = new FakeEngine();
        using var cancellation = new CancellationTokenSource();
        var waiting = TensorModelHost.AwaitStateAsync(engine, () => Task.CompletedTask, TensorEngineState.Loaded, cancellation.Token);
        engine.Transition(TensorEngineState.Failed, new Exception("sensitive error"));
        await Assert.ThrowsAsync<GpuException>(() => waiting);
        engine.Transition(TensorEngineState.Started);
        var second = TensorModelHost.AwaitStateAsync(engine, () => Task.CompletedTask, TensorEngineState.Loaded, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        engine.Transition(TensorEngineState.Loaded);
    }

    [Fact]
    public void CorruptWaveIsRejectedAtAudioBoundary()
    {
        using var audio = new MemoryStream([1, 2, 3, 4]);
        var error = Assert.Throws<ApiException>(() => MediaApi.DecodeWave(audio));
        Assert.Equal(400, error.Status);
    }
    [Fact]
    public async Task RefusalHasResponsesContentAndEvents()
    {
        var context = ApiRig.Context("/v1/responses");
        var output = new ResponsesOutput("r", "m", 1);
        await output.StartAsync(context, CancellationToken.None);
        await output.EventAsync(new LlmRefusalDeltaEvent("Declined"), context, CancellationToken.None);
        var result = await output.CompleteAsync(new() { Text = "", Refusal = "Declined", Status = LlmCompletionStatus.Refused, Provenance = new() { ImplementationId = "fake", ModelId = "m" } }, context, CancellationToken.None);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal("Declined", json.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("refusal").GetString());
        context.Response.Body.Position = 0;
        var events = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("event: response.refusal.delta", events);
        Assert.Contains("event: response.refusal.done", events);
    }

    [Fact]
    public async Task QueuedCancellationRemovesWaiterBeforeExecution()
    {
        await using var rig = new ApiRig();
        using var cancellation = new CancellationTokenSource();
        var gate = new AdmissionGate(rig.Coordinator, new(), TimeProvider.System);
        var admission = gate.AcquireAsync("public-model", ApiRig.Context(token: cancellation.Token), true);
        Assert.Single(rig.Coordinator.Snapshot().Waiters);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission);
        Assert.Empty(rig.Coordinator.Snapshot().Waiters);
        Assert.Empty(rig.Models.Engine.Requests);
    }

    [Fact]
    public async Task ImageMapsAliasAndReturnsEngineBytes()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        var context = ApiRig.Context("/v1/images/generations");
        var api = new MediaApi(rig.Models, new(rig.Coordinator, new(), TimeProvider.System), TimeProvider.System);
        using var request = JsonDocument.Parse("{\"model\":\"public-model\",\"prompt\":\"Sky\",\"size\":\"512x512\",\"seed\":42}");
        await api.ImagesAsync(context, request.RootElement);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("AQID", json.RootElement.GetProperty("data")[0].GetProperty("b64_json").GetString());
        Assert.Equal("actual-model", rig.Models.Engine.ImageRequest!.ModelId);
        Assert.Equal(42, rig.Models.Engine.ImageRequest.Seed);
        Assert.Equal(0, rig.Coordinator.Snapshot().LlmInFlight);
    }

    [Fact]
    public async Task TranscriptionDecodesWaveAndMapsTranslationAndSegments()
    {
        await using var rig = new ApiRig(); await rig.StartAsync();
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
        {
            writer.Write("RIFF"u8); writer.Write(38); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(2); writer.Write((short)16384);
        }
        bytes.Position = 0;
        var context = ApiRig.Context("/v1/audio/translations");
        context.Request.ContentType = "multipart/form-data; boundary=test";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["model"] = "public-model", ["response_format"] = "verbose_json" }, new FormFileCollection { new FormFile(bytes, 0, bytes.Length, "file", "audio.wav") });
        var api = new MediaApi(rig.Models, new(rig.Coordinator, new(), TimeProvider.System), TimeProvider.System);
        await api.AudioAsync(context);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("translate", json.RootElement.GetProperty("task").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("segments")[0].GetProperty("end").GetDouble());
        Assert.True(rig.Models.Engine.SpeechRequest!.Options!.TranslateToEnglish);
        Assert.Equal(0.5f, rig.Models.Engine.SpeechRequest.Audio.Samples.Span[0]);
        Assert.Equal(0, rig.Coordinator.Snapshot().LlmInFlight);
    }

}
