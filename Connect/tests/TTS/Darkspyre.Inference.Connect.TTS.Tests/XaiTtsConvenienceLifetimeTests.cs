using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
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

    [Fact]
    public async Task ActualInputCleanupCancellationIsNotHealthyInterruption()
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var original = new OperationCanceledException("actual cleanup cancellation", new CancellationToken(true));
        var input = new FailingInput(null, original);
        var socket = new XaiTtsStreamingTests.Socket();
        ITtsClient client = XaiTtsStreamingTests.Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        var events = Assert.IsAssignableFrom<ITtsStreamingClient>(client).SpeakStreamAsync(input, new(Language: "en"), Limits).GetAsyncEnumerator();
        try
        {
            var error = Assert.IsType<InferenceResourceOperationCanceledException>(await Record.ExceptionAsync(() => events.MoveNextAsync().AsTask().WaitAsync(Bound)));
            Assert.Same(original, error.InnerException); Assert.Equal(InferenceResourceOperationPhase.Cleanup, error.Failure.Phase);
            Assert.Same(error, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
            Assert.Same(error, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
            Assert.Equal(1, input.Reader!.Disposals); Assert.Equal(0, input.BorrowedDisposals); Assert.Equal(1, socket.Disposals);
        }
        finally
        {
            try { _ = await Record.ExceptionAsync(() => events.DisposeAsync().AsTask()); }
            finally { _ = await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryInputFailureDoesNotPoisonRetirementButActualCleanupRefusalsDo(bool refuses)
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var operation = new InvalidOperationException("actual input failure");
        var inputCleanup = new InvalidOperationException("actual input cleanup refusal");
        var socketCleanup = new InvalidOperationException("actual socket cleanup refusal");
        var input = new FailingInput(operation, refuses ? inputCleanup : null);
        var socket = new XaiTtsStreamingTests.Socket { CleanupFailure = refuses ? socketCleanup : null };
        ITtsClient client = XaiTtsStreamingTests.Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiTtsTests.Connection, new(new("selected"), access));
        var events = Assert.IsAssignableFrom<ITtsStreamingClient>(client).SpeakStreamAsync(input, new(Language: "en"), Limits).GetAsyncEnumerator();
        try
        {
            Exception? error = await Record.ExceptionAsync(() => events.MoveNextAsync().AsTask().WaitAsync(Bound));
            if (!refuses) { Assert.Same(operation, error); await client.DisposeAsync(); }
            else
            {
                var failures = Assert.IsType<AggregateException>(error).InnerExceptions;
                Assert.Equal(2, failures.Count);
                var inputFailures = Assert.IsType<AggregateException>(failures[0]).InnerExceptions;
                Assert.Equal(2, inputFailures.Count); Assert.Same(operation, inputFailures[0]);
                Assert.Same(inputCleanup, Assert.IsType<InferenceResourceOperationException>(inputFailures[1]).InnerException);
                Assert.Same(socketCleanup, Assert.IsType<InferenceResourceOperationException>(failures[1]).InnerException);
                var retirement = Assert.IsType<AggregateException>(await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
                Assert.Equal(2, retirement.InnerExceptions.Count);
                Assert.Same(inputFailures[1], retirement.InnerExceptions[0]); Assert.Same(failures[1], retirement.InnerExceptions[1]);
                Assert.Same(retirement, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
            }
            Assert.Equal(1, input.Reader!.Disposals); Assert.Equal(0, input.BorrowedDisposals); Assert.Equal(1, socket.Disposals);
        }
        finally
        {
            try { _ = await Record.ExceptionAsync(() => events.DisposeAsync().AsTask()); }
            finally { _ = await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()); }
        }
    }

    private sealed class FailingInput(Exception? operation, Exception? cleanup) : IAsyncEnumerable<string>, IDisposable
    {
        internal Enumerator? Reader;
        internal int BorrowedDisposals;
        public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken cancellationToken = default) => Reader = new(operation, cleanup);
        public void Dispose() => BorrowedDisposals++;
        internal sealed class Enumerator(Exception? operation, Exception? cleanup) : IAsyncEnumerator<string>
        {
            internal int Disposals;
            public string Current => throw new InvalidOperationException("No current input.");
            public ValueTask<bool> MoveNextAsync() => operation is null ? ValueTask.FromResult(false) : ValueTask.FromException<bool>(operation);
            public ValueTask DisposeAsync() { Disposals++; return cleanup is null ? ValueTask.CompletedTask : ValueTask.FromException(cleanup); }
        }
    }
}
