using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class XaiTtsStreamingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly InferenceStreamLimits Limits = new(8, 4096, 4096);
    private static readonly string[] ConvenienceCommands = ["text.delta", "text.done"];

    [Fact]
    public async Task ReusableSessionPreservesCommandsActualEventsAndHandedOffOwnership()
    {
        var source = new XaiTtsTests.Source(); using var access = new LlmConnectionAccess(source, []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException("No REST request.")));
        var socket = new Socket(); Uri? opened = null;
        await using ITtsClient client = Provider(http, (uri, headers, _) =>
        {
            opened = uri; Assert.Equal("controlled-token", headers.Headers.Authorization!.Parameter);
            return Task.FromResult<WebSocket>(socket);
        }).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        await using ITtsStreamingSession session = await Assert.IsAssignableFrom<ITtsStreamingClient>(client).OpenStreamAsync(
            new(Language: "en", Format: TtsAudioFormat.Pcm, SampleRate: 24000, IncludeTimestamps: true, Xai: new(2, false)), Limits);
        Assert.Equal("wss", opened!.Scheme); Assert.Contains("optimize_streaming_latency=2", opened.Query, StringComparison.Ordinal);
        Assert.Contains("with_timestamps=true", opened.Query, StringComparison.Ordinal);
        await client.DisposeAsync();
        await using var events = session.Events.GetAsyncEnumerator();
        await session.AppendTextAsync("first"); await session.CompleteUtteranceAsync();
        socket.Push("{\"type\":\"audio.delta\",\"delta\":\"AQID\",\"audio_duration\":0.25,\"audio_timestamps\":{\"graph_chars\":[\"a\"],\"graph_times\":[[1.5,1.75]]}}");
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout));
        var chunk = Assert.IsType<TtsAudioChunk>(events.Current); Assert.Equal(new byte[] { 1, 2, 3 }, chunk.Audio.ToArray());
        Assert.Null(chunk.ContentType); Assert.Equal(TimeSpan.FromSeconds(1.5), Assert.Single(chunk.Timestamps!).Start);
        socket.Push("{\"type\":\"audio.done\",\"trace_id\":\"actual-trace\"}");
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout)); Assert.Equal("actual-trace", Assert.IsType<TtsUtteranceCompleted>(events.Current).ProviderTraceId);
        await session.UpdateReplacementsAsync(new Dictionary<string, string>());
        socket.Push("{\"type\":\"session.updated\",\"replace\":{}}");
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout)); Assert.Empty(Assert.IsType<TtsReplacementsUpdated>(events.Current).Replace);
        await session.AppendTextAsync("second"); await session.ClearUtteranceAsync();
        socket.Push("{\"type\":\"audio.clear\"}");
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout)); Assert.IsType<TtsUtteranceCleared>(events.Current);
        await session.AppendTextAsync("third"); await session.CompleteUtteranceAsync();
        socket.Push("{\"type\":\"audio.done\"}");
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout)); Assert.Null(Assert.IsType<TtsUtteranceCompleted>(events.Current).ProviderTraceId);
        Assert.Equal(1, source.Calls); Assert.Equal(0, socket.Disposals);
        Assert.Equal("text.delta", Type(socket.Sent[0])); Assert.Equal("text.done", Type(socket.Sent[1])); Assert.Equal("session.update", Type(socket.Sent[2]));
        await session.DisposeAsync(); Assert.Equal(1, socket.Disposals);
    }

    [Fact]
    public async Task StreamingCommitsOneIncrementalHandleOnlyAtActualDone()
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var socket = new Socket(); var resources = new VoiceResourceFixture();
        await using ITtsClient client = Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiTtsTests.Connection, new(new("selected"), access), resources);
        await using ITtsStreamingSession session = await Assert.IsAssignableFrom<ITtsStreamingClient>(client).OpenStreamAsync(new(Language: "en"), Limits);
        await using var events = session.Events.GetAsyncEnumerator();
        await session.AppendTextAsync("actual"); await session.CompleteUtteranceAsync();
        socket.Push("{\"type\":\"audio.delta\",\"delta\":\"AQI=\"}");
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout)); Assert.Equal(0, resources.CommitCalls);
        socket.Push("{\"type\":\"audio.delta\",\"delta\":\"Aw==\"}");
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout)); Assert.Equal(0, resources.CommitCalls);
        socket.Push("{\"type\":\"audio.done\"}");
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout));
        var receipt = Assert.IsType<TtsUtteranceCompleted>(events.Current).Resource;
        Assert.NotNull(receipt); Assert.Null(receipt.ContentType); Assert.Equal(3, receipt.Bytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, resources.Committed); Assert.True(resources.Selected); Assert.Equal(1, resources.CommitCalls);
        Assert.Equal(1, resources.HandleDisposals); Assert.Equal(0, resources.BorrowedDisposals);
    }

    [Fact]
    public async Task ClearDisposesStagingWithoutPublishingOrSelectingResource()
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var socket = new Socket(); var resources = new VoiceResourceFixture();
        await using ITtsClient client = Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiTtsTests.Connection, new(new("selected"), access), resources);
        await using ITtsStreamingSession session = await Assert.IsAssignableFrom<ITtsStreamingClient>(client).OpenStreamAsync(new(Language: "en"), Limits);
        await using var events = session.Events.GetAsyncEnumerator();
        await session.AppendTextAsync("unfinished"); socket.Push("{\"type\":\"audio.delta\",\"delta\":\"AQ==\"}");
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout));
        await session.ClearUtteranceAsync(); socket.Push("{\"type\":\"audio.clear\"}");
        Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout)); Assert.IsType<TtsUtteranceCleared>(events.Current);
        Assert.Equal(0, resources.CommitCalls); Assert.Null(resources.Committed); Assert.False(resources.Selected); Assert.Equal(1, resources.HandleDisposals);
    }

    [Fact]
    public async Task RetirementDrainsIgnoringSendAndSharesCleanupFailure()
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var original = new InvalidOperationException("controlled socket cleanup"); var socket = new Socket { HoldSend = true, CleanupFailure = original };
        await using ITtsClient client = Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        ITtsStreamingSession session = await Assert.IsAssignableFrom<ITtsStreamingClient>(client).OpenStreamAsync(new(Language: "en"), Limits);
        Task send = session.AppendTextAsync("held"); Task? first = null; Task? second = null;
        try
        {
            await socket.SendEntered.Task.WaitAsync(Timeout); first = session.DisposeAsync().AsTask(); second = session.DisposeAsync().AsTask();
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted); Assert.Equal(0, socket.Disposals);
            socket.SendRelease.TrySetResult(); Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => send));
            var error = Assert.IsType<Darkspyre.Inference.Common.Resources.InferenceResourceOperationException>(await Record.ExceptionAsync(() => first));
            Assert.Same(original, error.InnerException); Assert.Same(error, await Record.ExceptionAsync(() => second)); Assert.Equal(1, socket.Disposals);
        }
        finally
        {
            first ??= session.DisposeAsync().AsTask(); socket.SendRelease.TrySetResult();
            _ = await Record.ExceptionAsync(() => send); _ = await Record.ExceptionAsync(() => first); if (second is not null) _ = await Record.ExceptionAsync(() => second);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateUnhandedSocketCleanupIsOnceAndRefusalIsRetained(bool fails)
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new InvalidOperationException("actual unhanded socket refusal");
        var socket = new Socket { CleanupFailure = fails ? cleanup : null };
        ITtsClient client = Provider(http, async (_, _, _) => { entered.TrySetResult(); await release.Task; return socket; }).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        Task<ITtsStreamingSession> opening = Assert.IsAssignableFrom<ITtsStreamingClient>(client).OpenStreamAsync(new(Language: "en"), Limits);
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(Timeout); retirement = client.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted); release.TrySetResult();
            Exception? error = await Record.ExceptionAsync(() => opening);
            if (fails)
            {
                var aggregate = Assert.IsType<AggregateException>(error);
                Assert.IsAssignableFrom<OperationCanceledException>(aggregate.InnerExceptions[0]);
                var refused = Assert.IsType<Darkspyre.Inference.Common.Resources.InferenceResourceOperationException>(aggregate.InnerExceptions[1]);
                Assert.Same(cleanup, refused.InnerException);
                Assert.Same(refused, await Record.ExceptionAsync(() => retirement));
                Assert.Same(refused, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
            }
            else { Assert.IsAssignableFrom<OperationCanceledException>(error); await retirement.WaitAsync(Timeout); }
            Assert.Equal(1, socket.Disposals);
        }
        finally
        {
            retirement ??= client.DisposeAsync().AsTask(); release.TrySetResult();
            try { _ = await Record.ExceptionAsync(() => opening.WaitAsync(Timeout)); }
            finally { _ = await Record.ExceptionAsync(() => retirement.WaitAsync(Timeout)); }
        }
    }

    [Fact]
    public async Task PausedEventsDoNotBlockRetirementAndSecondReaderRefuses()
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var socket = new Socket(); await using ITtsClient client = Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        await using ITtsStreamingSession session = await Assert.IsAssignableFrom<ITtsStreamingClient>(client).OpenStreamAsync(new(Language: "en"), new(1, 4, 4096));
        await session.AppendTextAsync("actual"); socket.Push("{\"type\":\"audio.delta\",\"delta\":\"AQID\"}");
        await using var events = session.Events.GetAsyncEnumerator(); Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Timeout));
        await using var second = session.Events.GetAsyncEnumerator(); await Assert.ThrowsAsync<InvalidOperationException>(() => second.MoveNextAsync().AsTask());
        socket.Push("{\"type\":\"audio.delta\",\"delta\":\"AQID\"}"); socket.Push("{\"type\":\"audio.delta\",\"delta\":\"AQID\"}");
        await session.DisposeAsync().AsTask().WaitAsync(Timeout); Assert.Equal(1, socket.Disposals);
    }

    [Fact]
    public async Task ConvenienceStreamPreservesControlAndTerminalObservations()
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var socket = new Socket();
        await using ITtsClient client = Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        socket.OnSend = value =>
        {
            if (Type(value) != "text.done") return;
            socket.Push("{\"type\":\"session.updated\",\"replace\":{}}");
            socket.Push("{\"type\":\"audio.delta\",\"delta\":\"AQ==\"}");
            socket.Push("{\"type\":\"audio.done\",\"trace_id\":\"actual\"}");
        };
        var events = new List<TtsStreamEvent>();
        await foreach (var item in Assert.IsAssignableFrom<ITtsStreamingClient>(client).SpeakStreamAsync(Text(), new(Language: "en"), Limits)) events.Add(item);
        Assert.Collection(events, item => Assert.IsType<TtsReplacementsUpdated>(item), item => Assert.IsType<TtsAudioChunk>(item), item => Assert.IsType<TtsUtteranceCompleted>(item));
        Assert.Equal(1, socket.Disposals); Assert.Equal(ConvenienceCommands, socket.Sent.Select(Type));
        static async IAsyncEnumerable<string> Text() { yield return "actual"; await Task.CompletedTask; }
    }

    [Theory]
    [InlineData("{\"type\":\"audio.delta\",\"delta\":null}")]
    [InlineData("{\"type\":\"audio.delta\",\"delta\":\"AQ==\",\"audio_duration\":-1}")]
    [InlineData("{\"type\":\"session.updated\",\"replace\":null}")]
    [InlineData("{\"type\":\"invented-terminal\"}")]
    public async Task InvalidActualObservationsNeverInventCompletion(string frame)
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var socket = new Socket();
        await using ITtsClient client = Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        await using ITtsStreamingSession session = await Assert.IsAssignableFrom<ITtsStreamingClient>(client).OpenStreamAsync(new(Language: "en"), Limits);
        await session.AppendTextAsync("actual"); await using var events = session.Events.GetAsyncEnumerator(); socket.Push(frame);
        var error = Assert.IsType<InferenceException>(await Record.ExceptionAsync(() => events.MoveNextAsync().AsTask().WaitAsync(Timeout)));
        Assert.Equal(InferenceFailureCode.ProtocolFailure, error.Code); await session.DisposeAsync(); Assert.Equal(1, socket.Disposals);
    }

    internal static ITtsProvider Provider(HttpClient http, Func<Uri, HttpRequestMessage, CancellationToken, Task<WebSocket>> connect)
    {
        Type type = typeof(ITtsProvider).Assembly.GetType("Darkspyre.Inference.Connect.TTS.XaiTtsProvider", throwOnError: true)!;
        ConstructorInfo constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(HttpClient), connect.GetType()], null)!;
        return Assert.IsAssignableFrom<ITtsProvider>(constructor.Invoke([http, connect]));
    }
    private static string Type(string value) { using JsonDocument document = JsonDocument.Parse(value); return document.RootElement.GetProperty("type").GetString()!; }

    internal sealed class Socket : WebSocket
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly List<string> _sent = [];
        private WebSocketState _state = WebSocketState.Open;
        internal int Disposals;
        internal bool HoldSend { get; init; }
        internal Exception? CleanupFailure { get; init; }
        internal Action<string>? OnSend { get; set; }
        internal TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SendRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal IReadOnlyList<string> Sent { get { lock (_sent) return _sent.ToArray(); } }
        internal void Push(string frame) => Assert.True(_incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(frame)));
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            byte[] frame = await _incoming.Reader.ReadAsync(cancellationToken); frame.AsSpan().CopyTo(buffer.AsSpan()); return new(frame.Length, WebSocketMessageType.Text, true);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SendEntered.TrySetResult(); if (HoldSend) await SendRelease.Task;
            string text = Encoding.UTF8.GetString(buffer);
            lock (_sent) _sent.Add(text);
            OnSend?.Invoke(text);
        }
        public override void Dispose() { Disposals++; _state = WebSocketState.Closed; if (CleanupFailure is not null) throw CleanupFailure; }
    }
}
