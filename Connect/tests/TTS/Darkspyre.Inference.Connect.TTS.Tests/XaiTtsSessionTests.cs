using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.Voice.Tests;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class XaiTtsSessionTests
{
    private static readonly string[] RepeatedCommands = ["text.delta", "text.done", "text.delta", "text.done"];
    private static Task<XaiTtsSession> OpenAsync(VoiceSocket socket, VoiceResources? resources = null, int events = 16)
        => XaiTtsSession.OpenAsync(socket.ConnectAsync, VoiceTestData.Connection,
            new() { Transport = TtsTransport.XaiTts, Endpoint = new(VoiceTestData.Connection.Endpoint) },
            new(Language: "en"), VoiceTestData.Limits(events), resources, () => { }, _ => { }, default);

    [Fact]
    public async Task CommandsSendWithoutReaderAndActualCompletionAllowsAnotherUtterance()
    {
        using var socket = new VoiceSocket();
        await using var session = await OpenAsync(socket);
        await session.AppendTextAsync("first").WaitAsync(VoiceTestData.Timeout);
        await session.CompleteUtteranceAsync().WaitAsync(VoiceTestData.Timeout);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AppendTextAsync("too early"));
        socket.Json("""{"type":"audio.delta","delta":"AQID"}""");
        socket.Json("""{"type":"audio.done","trace_id":"first"}""");
        await using var events = session.Events.GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<TtsAudioChunk>(events.Current).Audio.ToArray());
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        Assert.Equal("first", Assert.IsType<TtsUtteranceCompleted>(events.Current).ProviderTraceId);
        await session.AppendTextAsync("second");
        await session.CompleteUtteranceAsync();
        Assert.Equal(RepeatedCommands, socket.Sent.Select(frame => VoiceTestData.Type(frame.Bytes)));
        Assert.False(socket.Disposed);
    }

    [Fact]
    public async Task PausedReaderDoesNotPreventBoundedProducerRetirement()
    {
        using var socket = new VoiceSocket();
        await using var session = await OpenAsync(socket, events: 1);
        await session.AppendTextAsync("speech");
        socket.Json("""{"type":"audio.delta","delta":"AQ=="}""");
        await using var events = session.Events.GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        socket.Json("""{"type":"audio.delta","delta":"Ag=="}""");
        socket.Json("""{"type":"audio.delta","delta":"Aw=="}""");
        await socket.WaitReceivedAsync(3);
        await session.DisposeAsync().AsTask().WaitAsync(VoiceTestData.Timeout);
        Assert.True(socket.Disposed);
        Assert.False(await events.MoveNextAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.AppendTextAsync("late"));
    }

    [Fact]
    public async Task RetirementWaitsForIgnoringArtifactWriteBeforeReleasingItsHandle()
    {
        using var socket = new VoiceSocket();
        var stream = new HeldStream([], holdWrite: true);
        var resources = new VoiceResources { Writer = new() { Stream = stream } };
        var session = await OpenAsync(socket, resources);
        Task? retirement = null;
        try
        {
            await session.AppendTextAsync("speech");
            socket.Json("""{"type":"audio.delta","delta":"AQID"}""");
            await stream.Entered.Task.WaitAsync(VoiceTestData.Timeout);
            retirement = session.DisposeAsync().AsTask();
            await stream.Canceled.Task.WaitAsync(VoiceTestData.Timeout);
            Assert.False(retirement.IsCompleted);
            Assert.False(resources.Writer.Disposed.Task.IsCompleted);
        }
        finally { stream.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retirement!.WaitAsync(VoiceTestData.Timeout));
        Assert.True(resources.Writer.Disposed.Task.IsCompleted);
        Assert.True(socket.Disposed);
        Assert.Equal(0, resources.Writer.CommitCount);
    }

    [Fact]
    public async Task ActualCommittedAudioSurvivesCleanupFailureWithoutRetry()
    {
        using var socket = new VoiceSocket();
        var cleanup = new IOException("owned writer cleanup failed");
        var resources = new VoiceResources { Writer = new() { CleanupFailure = cleanup } };
        var session = await OpenAsync(socket, resources);
        await session.AppendTextAsync("speech");
        socket.Json("""{"type":"audio.delta","delta":"AQID"}""");
        socket.Json("""{"type":"audio.done"}""");
        await using var events = session.Events.GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        var failure = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => events.MoveNextAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        Assert.Equal(InferenceResourceOperationPhase.Cleanup, failure.Failure.Phase);
        Assert.Same(cleanup, failure.InnerException);
        var receipt = Assert.Single(failure.Failure.GeneratedResources);
        Assert.Equal(resources.Writer.Reference, receipt.Resource.Reference);
        Assert.Equal(1, receipt.Selection!.Revision);
        Assert.Equal(new byte[] { 1, 2, 3 }, resources.Writer.Bytes);
        var first = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => session.DisposeAsync().AsTask().WaitAsync(VoiceTestData.Timeout));
        var second = await Assert.ThrowsAsync<InferenceResourceOperationException>(() => session.DisposeAsync().AsTask());
        Assert.Same(first, second);
        Assert.Equal(1, resources.Writer.CommitCount);
        Assert.True(socket.Disposed);
    }
}
