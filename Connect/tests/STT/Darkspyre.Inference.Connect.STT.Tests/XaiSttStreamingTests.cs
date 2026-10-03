using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.STT.Tests;

public sealed class XaiSttStreamingTests
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    internal static readonly InferenceStreamLimits Limits = new(8, 4096, 4096);
    private static readonly int?[] ChannelOrder = [1, 0];

    [Fact]
    public async Task OpeningWaitsForReadinessAndPreservesCreatedForReader()
    {
        using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []); using var http = Http();
        var socket = new Socket(); Uri? uri = null;
        await using var client = Provider(http, (value, headers, _) =>
        { uri = value; Assert.Equal("actual-token", headers.Headers.Authorization!.Parameter); return Task.FromResult<WebSocket>(socket); }).Bind(XaiSttTests.Connection, new(new("selected"), access));
        Task<ISpeechTranscriptionSession> opening = client.OpenStreamAsync(new(Language: "en", InterimResults: false, Endpointing: TimeSpan.Zero, VadThreshold: 0), Limits);
        try
        {
            await socket.ReceiveEntered.Task.WaitAsync(Deadline); Assert.False(opening.IsCompleted); Assert.Empty(socket.Sent);
            socket.Created(); await using var session = await opening.WaitAsync(Deadline);
            Assert.Contains("endpointing=0", uri!.Query, StringComparison.Ordinal); Assert.Contains("interim_results=false", uri.Query, StringComparison.Ordinal);
            Assert.Contains("language=en", uri.Query, StringComparison.Ordinal); Assert.DoesNotContain("model=", uri.Query, StringComparison.Ordinal);
            await using var reader = session.Events.GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline));
            Assert.Equal("actual-session", Assert.IsType<SpeechTranscriptCreatedEvent>(reader.Current).SessionId);
        }
        finally { if (!opening.IsCompleted) await client.DisposeAsync(); _ = await Record.ExceptionAsync(() => opening); }
    }

    [Fact]
    public async Task ReusableFinalizeAndEveryChannelFinalRemainOrderedThroughClosure()
    {
        using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []); using var http = Http(); var socket = new Socket(); socket.Created();
        await using var client = Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiSttTests.Connection, new(new("selected"), access));
        await using var session = await client.OpenStreamAsync(new(Multichannel: true, Channels: 2), Limits);
        await client.DisposeAsync();
        await session.AppendAudioAsync(new byte[] { 1, 2 }); await session.FinalizeAsync(); await session.AppendAudioAsync(new byte[] { 3, 4 }); await session.FinalizeAsync(1);
        Assert.Equal(WebSocketMessageType.Binary, socket.Sent[0].Type); Assert.Equal(new byte[] { 1, 2 }, socket.Sent[0].Bytes);
        using (var all = JsonDocument.Parse(socket.Sent[1].Bytes)) Assert.False(all.RootElement.TryGetProperty("channel", out _));
        using (var one = JsonDocument.Parse(socket.Sent[3].Bytes)) Assert.Equal(1, one.RootElement.GetProperty("channel").GetInt32());
        await session.CompleteAudioAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AppendAudioAsync(new byte[] { 5 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.FinalizeAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompleteAudioAsync());
        socket.Push("{\"type\":\"transcript.partial\",\"text\":\"snapshot\",\"start\":0,\"duration\":0.5,\"is_final\":true,\"speech_final\":false,\"channel_index\":1,\"words\":[]}");
        socket.Done(1); socket.Done(0); socket.End();
        var observations = new List<SpeechTranscriptEvent>(); await foreach (var observation in session.Events) observations.Add(observation);
        Assert.Equal(4, observations.Count); var partial = Assert.IsType<SpeechTranscriptPartialEvent>(observations[1]);
        Assert.True(partial.IsFinal); Assert.False(partial.SpeechFinal); Assert.Empty(partial.Words!);
        Assert.Equal(ChannelOrder, observations.OfType<SpeechTranscriptDoneEvent>().Select(item => item.ChannelIndex));
        Assert.Equal(0, socket.Disposals); await session.DisposeAsync(); Assert.Equal(1, socket.Disposals);
    }

    [Theory]
    [InlineData("normalization")]
    [InlineData("endpointing")]
    [InlineData("smart")]
    [InlineData("channels")]
    [InlineData("opus")]
    public async Task UnsupportedOrInvalidControlsRefuseBeforeSourceAndSocket(string control)
    {
        var source = new XaiSttTests.Source(); using var access = new LlmConnectionAccess(source, []); using var http = Http(); int openings = 0;
        await using var client = Provider(http, (_, _, _) => { openings++; throw new InvalidOperationException(); }).Bind(XaiSttTests.Connection, new(new("selected"), access));
        SpeechStreamOptions options = control switch
        {
            "normalization" => new(TextNormalization: false),
            "endpointing" => new(Endpointing: TimeSpan.FromTicks(1)),
            "smart" => new(SmartTurnTimeout: TimeSpan.FromMilliseconds(2)),
            "channels" => new(Multichannel: true),
            _ => new(Encoding: SpeechAudioEncoding.Opus, Multichannel: true, Channels: 2)
        };
        Assert.NotNull(await Record.ExceptionAsync(() => client.OpenStreamAsync(options, Limits)));
        Assert.Equal(0, source.Calls); Assert.Equal(0, openings);
        var capabilities = await client.GetCapabilitiesAsync("manual-model"); Assert.False(capabilities.Streaming.SupportsTextNormalization); Assert.True(capabilities.Transcription.SupportsTextNormalization);
    }

    [Fact]
    public async Task OpusPacketAndExplicitIgnoredSampleRateAreNotTransformed()
    {
        using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []); using var http = Http(); var socket = new Socket(); socket.Created(); Uri? opened = null;
        await using var client = Provider(http, (uri, _, _) => { opened = uri; return Task.FromResult<WebSocket>(socket); }).Bind(XaiSttTests.Connection, new(new("selected"), access));
        await using var session = await client.OpenStreamAsync(new(Encoding: SpeechAudioEncoding.Opus, SampleRate: 24000), Limits);
        await session.AppendAudioAsync(new byte[] { 1, 2, 3 }); Assert.Single(socket.Sent); Assert.True(socket.Sent[0].End);
        Assert.Equal(new byte[] { 1, 2, 3 }, socket.Sent[0].Bytes); Assert.Contains("sample_rate=24000", opened!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PausedReaderDoesNotBlockRetirementOrPermitASecondReader()
    {
        using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []); using var http = Http(); var socket = new Socket(); socket.Created();
        await using var client = Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiSttTests.Connection, new(new("selected"), access));
        await using var session = await client.OpenStreamAsync(new(), new(1, 256, 4096));
        await using var reader = session.Events.GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline));
        await using var other = session.Events.GetAsyncEnumerator(); await Assert.ThrowsAsync<InvalidOperationException>(() => other.MoveNextAsync().AsTask());
        socket.Partial(); socket.Partial(); await session.DisposeAsync().AsTask().WaitAsync(Deadline); Assert.Equal(1, socket.Disposals);
        Assert.False(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline));
    }

    [Theory]
    [InlineData("{\"type\":\"transcript.partial\",\"text\":\"x\"}")]
    [InlineData("{\"type\":\"transcript.done\",\"text\":\"x\",\"words\":[]}")]
    [InlineData("{\"type\":\"invented.done\"}")]
    public async Task MalformedFramesNeverInventFinality(string frame)
    {
        using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []); using var http = Http(); var socket = new Socket(); socket.Created();
        await using var client = Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiSttTests.Connection, new(new("selected"), access));
        await using var session = await client.OpenStreamAsync(new(), Limits); await session.CompleteAudioAsync();
        await using var reader = session.Events.GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync()); socket.Push(frame);
        var error = Assert.IsType<InferenceException>(await Record.ExceptionAsync(() => reader.MoveNextAsync().AsTask().WaitAsync(Deadline)));
        Assert.Equal(InferenceFailureCode.ProtocolFailure, error.Code); await session.DisposeAsync();
    }

    [Fact]
    public async Task ProviderErrorIsSafeAndDoesNotGuessWhichErrorsKeepConnectionOpen()
    {
        using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []); using var http = Http(); var socket = new Socket(); socket.Created();
        await using var client = Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiSttTests.Connection, new(new("selected"), access));
        await using var session = await client.OpenStreamAsync(new(), Limits); await using var reader = session.Events.GetAsyncEnumerator(); Assert.True(await reader.MoveNextAsync());
        socket.Push("{\"type\":\"error\",\"message\":\"https://secret.test/token=unsafe\"}");
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)); var error = Assert.IsType<SpeechTranscriptErrorEvent>(reader.Current);
        Assert.DoesNotContain("secret", error.Error.Message, StringComparison.Ordinal); Assert.Null(error.Error.ProviderCode);
        await session.AppendAudioAsync(new byte[] { 1 }); socket.Partial(); Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)); Assert.IsType<SpeechTranscriptPartialEvent>(reader.Current);
    }

    [Fact]
    public async Task ConvenienceOwnsOneSessionAndPreservesAllEventsUntilRealClosure()
    {
        using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []); using var http = Http(); var socket = new Socket(); socket.Created();
        socket.OnSend = sent => { if (sent.Type == WebSocketMessageType.Text) { socket.Partial(); socket.Done(); socket.End(); } };
        await using var client = Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiSttTests.Connection, new(new("selected"), access));
        var events = new List<SpeechTranscriptEvent>(); await foreach (var observation in client.TranscribeStreamAsync(Audio(), new(), Limits)) events.Add(observation);
        Assert.Collection(events, value => Assert.IsType<SpeechTranscriptCreatedEvent>(value), value => Assert.IsType<SpeechTranscriptPartialEvent>(value), value => Assert.IsType<SpeechTranscriptDoneEvent>(value));
        Assert.Equal(1, socket.Disposals); Assert.Equal(2, socket.Sent.Count);
        static async IAsyncEnumerable<ReadOnlyMemory<byte>> Audio() { yield return new byte[] { 1 }; await Task.CompletedTask; }
    }

    [Fact]
    public async Task IgnoringInputDrainsWhileConvenienceIteratorIsPausedWithoutLateSend()
    {
        using var access = new LlmConnectionAccess(new XaiSttTests.Source(), []); using var http = Http(); var socket = new Socket(); socket.Created(); var input = new IgnoringInput();
        var client = Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiSttTests.Connection, new(new("selected"), access));
        var reader = client.TranscribeStreamAsync(input, new(), Limits).GetAsyncEnumerator(); Task? retirement = null;
        try
        {
            Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(Deadline)); await input.Entered.Task.WaitAsync(Deadline);
            retirement = client.DisposeAsync().AsTask(); await socket.Disposed.Task.WaitAsync(Deadline);
            Assert.False(retirement.IsCompleted); input.Release.TrySetResult(); await retirement.WaitAsync(Deadline);
            Assert.Empty(socket.Sent); Assert.Equal(1, input.EnumeratorDisposals); Assert.Equal(0, input.SourceDisposals);
        }
        finally
        {
            retirement ??= client.DisposeAsync().AsTask(); input.Release.TrySetResult();
            try { await retirement.WaitAsync(Deadline); } finally { await reader.DisposeAsync(); }
        }
    }

    internal static XaiSttProvider Provider(HttpClient http, Func<Uri, HttpRequestMessage, CancellationToken, Task<WebSocket>> connect)
        => new(http, (uri, headers, _, _, token) => connect(uri, headers, token));
    internal static HttpClient Http() => new(new XaiSttTests.Handler(_ => throw new InvalidOperationException("No REST call.")));
    internal sealed record SentFrame(byte[] Bytes, WebSocketMessageType Type, bool End);
    internal sealed class Socket : WebSocket
    {
        private readonly Channel<(byte[] Data, WebSocketMessageType Type)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
        private readonly List<SentFrame> _sent = []; private WebSocketState _state = WebSocketState.Open;
        internal IReadOnlyList<SentFrame> Sent { get { lock (_sent) return _sent.ToArray(); } }
        internal int Disposals; internal Exception? CleanupFailure { get; init; } internal Action<SentFrame>? OnSend;
        internal TaskCompletionSource ReceiveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Push(string json) => Assert.True(_incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text)));
        internal void Created() => Push("{\"type\":\"transcript.created\",\"id\":\"actual-session\"}");
        internal void Partial() => Push("{\"type\":\"transcript.partial\",\"text\":\"x\",\"start\":0,\"duration\":1,\"is_final\":false,\"speech_final\":false,\"words\":[]}");
        internal void Done(int? channel = null) => Push("{\"type\":\"transcript.done\",\"text\":\"x\",\"duration\":1,\"words\":[]" + (channel is null ? "" : ",\"channel_index\":" + channel) + "}");
        internal void End() => Assert.True(_incoming.Writer.TryWrite(([], WebSocketMessageType.Close)));
        public override WebSocketCloseStatus? CloseStatus => null; public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state; public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        { ReceiveEntered.TrySetResult(); var frame = await _incoming.Reader.ReadAsync(cancellationToken); frame.Data.AsSpan().CopyTo(buffer.AsSpan()); return new(frame.Data.Length, frame.Type, true); }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        { var frame = new SentFrame(buffer.ToArray(), messageType, endOfMessage); lock (_sent) _sent.Add(frame); OnSend?.Invoke(frame); return Task.CompletedTask; }
        public override void Dispose() { Disposals++; _state = WebSocketState.Closed; Disposed.TrySetResult(); if (CleanupFailure is not null) throw CleanupFailure; }
    }
    private sealed class IgnoringInput : IAsyncEnumerable<ReadOnlyMemory<byte>>, IDisposable
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int EnumeratorDisposals, SourceDisposals;
        public IAsyncEnumerator<ReadOnlyMemory<byte>> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(this);
        public void Dispose() => SourceDisposals++;
        private sealed class Enumerator(IgnoringInput owner) : IAsyncEnumerator<ReadOnlyMemory<byte>>
        {
            public ReadOnlyMemory<byte> Current => new byte[] { 1 };
            public async ValueTask<bool> MoveNextAsync() { owner.Entered.TrySetResult(); await owner.Release.Task; return true; }
            public ValueTask DisposeAsync() { owner.EnumeratorDisposals++; return ValueTask.CompletedTask; }
        }
    }
}
