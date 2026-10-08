using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Connect.Voice.Tests;

namespace Darkspyre.Inference.Connect.Realtime.Tests;

public sealed class XaiRealtimeSessionTests
{
    [Fact]
    public async Task FailedUpgradeRetainsSafeRequestIdAndRetryDelay()
    {
        using var handler = new RejectedVoiceUpgrade("/v1/realtime");
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("X-Default", "default-secret");
        await using var access = new LlmConnectionAccess(new VoiceConnectionSource(), []);
        var binding = new XaiFamilyBinding(new("selected"), access);
        await using var provider = new XaiRealtimeProvider(http);

        LlmProviderException failure = await Assert.ThrowsAsync<LlmProviderException>(() =>
            provider.OpenSessionAsync(new()
            {
                Transport = RealtimeTransport.XaiRealtime,
                Endpoint = new(VoiceTestData.Connection.Endpoint),
                Xai = binding.Selection,
                AdditionalHeaders = new Dictionary<string, string> { ["X-Family"] = "family-secret" }
            }, new() { TurnDetection = new RealtimeManualTurnDetection() }, VoiceTestData.Limits(), binding));

        VoiceTestData.AssertUpgradeFailure(failure);
        Assert.Equal(1, handler.Sends);
    }

    private static readonly string[] RepeatedCommands = ["session.update", "input_audio_buffer.append", "input_audio_buffer.commit", "response.create", "response.create"];
    private static async Task<IRealtimeVoiceSession> OpenAsync(VoiceSocket socket, VoiceResources? resources = null, int events = 16, string[]? secrets = null)
    {
        var config = new RealtimeSessionConfig { TurnDetection = new RealtimeManualTurnDetection() };
        var session = new XaiRealtimeSession(socket, VoiceTestData.Limits(events), resources,
            resources is null ? null : new(resources.WorkspaceId, resources.SessionId, 0), config,
            (_, _) => throw new InvalidOperationException("This test does not issue resumption receipts."), secrets ?? [], (_, _) => { }, _ => { }, null);
        await session.InitializeAsync(await XaiRealtimeProtocol.UpdateAsync(config, () => { }, default), default);
        return session;
    }

    [Theory]
    [InlineData("error", false)]
    [InlineData("response.mcp_call.failed", false)]
    [InlineData("mcp_list_tools.failed", true)]
    public async Task ProviderErrorDetailsSurviveWithSessionSecretsRedacted(string stage, bool stringError)
    {
        using var socket = new VoiceSocket();
        await using IRealtimeVoiceSession session = await OpenAsync(socket,
            secrets: ["access-token", "refresh-token", "header-secret", "tool-secret"]);
        const string detail = "Unsupported voice: access-token refresh-token header-secret tool-secret";
        string error = stringError ? JsonValue.Create(detail)!.ToJsonString()
            : $$"""{"code":"invalid_voice","message":"{{detail}}","param":"voice","event_id":"client-1"}""";
        socket.Json($$"""{"type":"{{stage}}","event_id":"server-1","error":{{error}}}""");

        await using var events = session.Events.GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        InferenceResourceFailure failure;
        if (stage == "error")
        {
            var observed = Assert.IsType<RealtimeErrorEvent>(events.Current);
            failure = observed.Error;
            Assert.Equal("voice", observed.Parameter);
            Assert.Equal("client-1", observed.RelatedClientEventId);
        }
        else
        {
            var observed = Assert.IsType<RealtimeHostedObservationEvent>(events.Current);
            failure = Assert.IsType<InferenceResourceFailure>(observed.Error);
            Assert.Equal(stage, observed.Stage);
        }
        Assert.Equal("server-1", events.Current.Identity.EventId);
        Assert.Equal(stringError ? null : "invalid_voice", failure.ProviderCode);
        Assert.Equal("Unsupported voice: [REDACTED] [REDACTED] [REDACTED] [REDACTED]", failure.Message);
        await session.CreateResponseAsync();
        Assert.False(socket.Disposed);
    }

    [Fact]
    public async Task ActualResponseCompletionDoesNotRetireReusableSessionOrDelayCommands()
    {
        using var socket = new VoiceSocket();
        await using IRealtimeVoiceSession session = await OpenAsync(socket);
        await session.AppendAudioAsync(new byte[] { 1, 2, 3 }).WaitAsync(VoiceTestData.Timeout);
        await session.CommitAudioAsync();
        await session.CreateResponseAsync();
        socket.Json("""{"type":"response.created","response":{"id":"r1"}}""");
        socket.Json("""{"type":"response.text.delta","response_id":"r1","delta":"hello"}""");
        socket.Json("""{"type":"response.done","response":{"id":"r1","status":"completed","usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}}}""");
        await using var events = session.Events.GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        Assert.Equal("response.created", Assert.IsType<RealtimeResponseEvent>(events.Current).Stage);
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        Assert.Equal("hello", Assert.IsType<RealtimeTextDeltaEvent>(events.Current).Text);
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        var completed = Assert.IsType<RealtimeResponseEvent>(events.Current);
        Assert.Equal(LlmCompletionStatus.Completed, completed.Status);
        Assert.Equal(3, completed.Usage!.TotalTokens);
        await session.CreateResponseAsync();
        Assert.Equal(RepeatedCommands,
            socket.Sent.Select(frame => VoiceTestData.Type(frame.Bytes)));
        Assert.False(socket.Disposed);
    }

    [Fact]
    public async Task PausedEventsReaderCannotHoldBoundedReceiverRetirement()
    {
        using var socket = new VoiceSocket();
        await using IRealtimeVoiceSession session = await OpenAsync(socket, events: 1);
        const string delta = """{"type":"response.text.delta","response_id":"r1","delta":"hello"}""";
        socket.Json(delta);
        await using var events = session.Events.GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        socket.Json(delta); socket.Json(delta);
        await socket.WaitReceivedAsync(3);
        await session.DisposeAsync().AsTask().WaitAsync(VoiceTestData.Timeout);
        Assert.True(socket.Disposed);
        Assert.False(await events.MoveNextAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.CreateResponseAsync());
    }

    [Fact]
    public async Task RetirementDrainsIgnoringToolResourceReadWithoutSendingLateFrames()
    {
        using var socket = new VoiceSocket();
        var resources = new VoiceResources();
        IRealtimeVoiceSession session = await OpenAsync(socket, resources);
        Task? command = null; Task? retirement = null;
        try
        {
            command = session.SubmitToolResultAsync(new("call-1", new()
            { Parts = [new LlmToolOutputSessionResource(resources.Reader.Resource.Reference)] }));
            await resources.Reader.Stream.Entered.Task.WaitAsync(VoiceTestData.Timeout);
            retirement = session.DisposeAsync().AsTask();
            await resources.Reader.Stream.Canceled.Task.WaitAsync(VoiceTestData.Timeout);
            Assert.False(retirement.IsCompleted);
            Assert.False(resources.Reader.Disposed);
        }
        finally { resources.Reader.Stream.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command!.WaitAsync(VoiceTestData.Timeout));
        await retirement!.WaitAsync(VoiceTestData.Timeout);
        Assert.True(resources.Reader.Disposed);
        Assert.True(socket.Disposed);
        Assert.Equal("session.update", VoiceTestData.Type(Assert.Single(socket.Sent).Bytes));
    }

    [Fact]
    public async Task FailedResponseCommittedAudioRemainsRecoverableAfterCleanupFailure()
    {
        using var socket = new VoiceSocket();
        var resources = new VoiceResources { Writer = new() { CleanupFailure = new IOException("owned writer refused cleanup") } };
        IRealtimeVoiceSession session = await OpenAsync(socket, resources);
        socket.Json("""{"type":"response.created","response":{"id":"r1"}}""");
        socket.Json("""{"type":"response.audio.delta","response_id":"r1","item_id":"item1","output_index":0,"content_index":0,"delta":"AQID"}""");
        socket.Json("""{"type":"response.done","response":{"id":"r1","status":"failed"}}""");
        await resources.Writer.Disposed.Task.WaitAsync(VoiceTestData.Timeout);
        var failure = await Assert.ThrowsAsync<RealtimeOperationException>(() => session.DisposeAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        Assert.Equal("failed", failure.Error.ProviderStatus);
        var artifact = Assert.Single(failure.Error.AudioArtifacts!);
        Assert.Equal("r1", artifact.Identity.ResponseId);
        Assert.Equal("item1", artifact.Identity.ItemId);
        Assert.Equal(resources.Writer.Reference, artifact.Resource.Reference);
        Assert.Equal(1, artifact.SelectionVersion!.Revision);
        Assert.Equal(new byte[] { 1, 2, 3 }, resources.Writer.Bytes);
        Assert.Equal(1, resources.Writer.CommitCount);
        var repeated = await Assert.ThrowsAsync<RealtimeOperationException>(() => session.DisposeAsync().AsTask());
        Assert.Same(failure, repeated);
        Assert.True(socket.Disposed);
    }
}
