using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class XaiTtsConvenienceLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly InferenceStreamLimits Limits = new(2, 2048, 2048);

    [Fact]
    public void DirectProviderRejectsWrongTransportBeforeEffects()
    {
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException("No HTTP permitted.")));
        ITtsClient? unexpected = null;
        try
        {
            Assert.Throws<ArgumentException>(() => unexpected = new XaiTtsProvider(http).Bind(new() { Endpoint = new("https://api.x.ai/v1") }));
        }
        finally { unexpected?.Dispose(); }
    }

    [Fact]
    public async Task ClientRetirementDrainsIgnoringConvenienceInputAndRefusesLateOutput()
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var socket = new XaiTtsStreamingTests.Socket();
        ITtsClient client = XaiTtsStreamingTests.Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        var entered = Signal(); var release = Signal();
        using var caller = new CancellationTokenSource();
        var events = Assert.IsAssignableFrom<ITtsStreamingClient>(client).SpeakStreamAsync(Input(caller.Token), new(Language: "en"), Limits, caller.Token).GetAsyncEnumerator();
        Task<bool> reading = events.MoveNextAsync().AsTask(); Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(Bound); retirement = client.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            release.TrySetResult();
            Exception? readError = await Record.ExceptionAsync(() => reading.WaitAsync(Bound));
            Assert.IsAssignableFrom<OperationCanceledException>(readError);
            await retirement.WaitAsync(Bound); Assert.Empty(socket.Sent); Assert.Equal(1, socket.Disposals);
        }
        finally
        {
            retirement ??= client.DisposeAsync().AsTask();
            await caller.CancelAsync(); release.TrySetResult();
            try { _ = await Record.ExceptionAsync(() => reading.WaitAsync(Bound)); }
            finally
            {
                try { _ = await Record.ExceptionAsync(() => events.DisposeAsync().AsTask().WaitAsync(Bound)); }
                finally { _ = await Record.ExceptionAsync(() => retirement.WaitAsync(Bound)); }
            }
        }
        async IAsyncEnumerable<string> Input([EnumeratorCancellation] CancellationToken token)
        {
            entered.TrySetResult(); await release.Task;
            yield return "must not send"; GC.KeepAlive(token);
        }
    }

    [Fact]
    public async Task PausedConvenienceYieldDoesNotBlockClientRetirement()
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var socket = new XaiTtsStreamingTests.Socket();
        socket.OnSend = _ => socket.Push("{\"type\":\"audio.delta\",\"delta\":\"AQ==\"}");
        ITtsClient client = XaiTtsStreamingTests.Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        using var caller = new CancellationTokenSource();
        var events = Assert.IsAssignableFrom<ITtsStreamingClient>(client).SpeakStreamAsync(Input(), new(Language: "en"), Limits, caller.Token).GetAsyncEnumerator();
        Task? retirement = null;
        try
        {
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Bound));
            Assert.IsType<TtsAudioChunk>(events.Current);
            retirement = client.DisposeAsync().AsTask(); await retirement.WaitAsync(Bound);
            Assert.Equal(1, socket.Disposals);
        }
        finally
        {
            retirement ??= client.DisposeAsync().AsTask(); await caller.CancelAsync();
            try { _ = await Record.ExceptionAsync(() => events.DisposeAsync().AsTask().WaitAsync(Bound)); }
            finally { _ = await Record.ExceptionAsync(() => retirement.WaitAsync(Bound)); }
        }
        static async IAsyncEnumerable<string> Input() { yield return "actual"; await Task.CompletedTask; }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
