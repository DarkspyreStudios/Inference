using System.Net.WebSockets;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM;

namespace Darkspyre.Inference.Connect.TTS.Tests;

public sealed class XaiTtsResourceRetirementTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly Darkspyre.Inference.Common.Media.InferenceStreamLimits Limits = new(2, 2048, 2048);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task LateCommitFactsSurviveRetirementWithoutLateObservation(bool lostAcknowledgment, bool refusesCleanup, bool convenience)
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var originalCleanup = new InvalidOperationException("actual late writer cleanup refusal");
        var resources = new HeldResources(lostAcknowledgment, refusesCleanup ? originalCleanup : null);
        var socket = new XaiTtsStreamingTests.Socket();
        ITtsClient client = XaiTtsStreamingTests.Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiTtsTests.Connection, new(new("selected"), access), resources);
        ITtsStreamingSession? session = null;
        IAsyncEnumerator<TtsStreamEvent>? events = null;
        Task<bool>? reading = null; Task? retirement = null;
        try
        {
            var streaming = Assert.IsAssignableFrom<ITtsStreamingClient>(client);
            if (convenience)
            {
                socket.OnSend = value =>
                {
                    if (!value.Contains("text.done", StringComparison.Ordinal)) return;
                    socket.Push("{\"type\":\"audio.delta\",\"delta\":\"AQ==\"}"); socket.Push("{\"type\":\"audio.done\"}");
                };
                events = streaming.SpeakStreamAsync(Input(), new(Language: "en"), Limits).GetAsyncEnumerator();
            }
            else
            {
                session = await streaming.OpenStreamAsync(new(Language: "en"), Limits);
                events = session.Events.GetAsyncEnumerator();
                await session.AppendTextAsync("actual"); await session.CompleteUtteranceAsync();
                socket.Push("{\"type\":\"audio.delta\",\"delta\":\"AQ==\"}"); socket.Push("{\"type\":\"audio.done\"}");
            }
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Bound)); Assert.IsType<TtsAudioChunk>(events.Current);
            await resources.Entered.Task.WaitAsync(Bound);
            reading = events.MoveNextAsync().AsTask();
            retirement = convenience ? client.DisposeAsync().AsTask() : session!.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            if (!convenience) Assert.False(await reading.WaitAsync(Bound));
            resources.Release.TrySetResult();
            Exception? error = await Record.ExceptionAsync(() => retirement.WaitAsync(Bound));
            InferenceResourceOperationCanceledException facts;
            if (refusesCleanup)
            {
                var aggregate = Assert.IsType<AggregateException>(error); Assert.Equal(2, aggregate.InnerExceptions.Count);
                facts = Assert.IsType<InferenceResourceOperationCanceledException>(aggregate.InnerExceptions[0]);
                var cleanup = Assert.IsType<InferenceResourceOperationException>(aggregate.InnerExceptions[1]);
                Assert.Equal(InferenceResourceOperationPhase.Cleanup, cleanup.Failure.Phase); Assert.Same(originalCleanup, cleanup.InnerException);
            }
            else facts = Assert.IsType<InferenceResourceOperationCanceledException>(error);
            Assert.Equal(InferenceResourceOperationPhase.Commit, facts.Failure.Phase); Assert.True(facts.CancellationToken.IsCancellationRequested);
            Assert.Equal(lostAcknowledgment ? InferenceResourceCommitOutcome.Unknown : InferenceResourceCommitOutcome.Committed, facts.Failure.CommitOutcome);
            Assert.Equal(lostAcknowledgment ? InferenceResourceCommitOutcome.Unknown : InferenceResourceCommitOutcome.Committed, facts.Failure.SelectionOutcome);
            Assert.Equal(new InferenceResourceReference("workspace", "audio"), facts.Failure.Reference);
            Assert.Equal(1, facts.Failure.BytesTransferred);
            if (lostAcknowledgment) Assert.Null(facts.Failure.SelectionVersion);
            else Assert.Equal(new InferenceResourceSelectionVersion("workspace", "session", 1), facts.Failure.SelectionVersion);
            Assert.Same(error, await Record.ExceptionAsync(() => convenience ? client.DisposeAsync().AsTask() : session!.DisposeAsync().AsTask()));
            if (convenience) Assert.Same(error, await Record.ExceptionAsync(() => reading.WaitAsync(Bound)));
            Assert.Equal(1, resources.Inner.CommitCalls); Assert.True(resources.Inner.Selected);
            Assert.Equal(1, resources.Inner.HandleDisposals); Assert.Equal(0, resources.Inner.BorrowedDisposals); Assert.Equal(1, socket.Disposals);
        }
        finally
        {
            retirement ??= session is null ? client.DisposeAsync().AsTask() : session.DisposeAsync().AsTask(); resources.Release.TrySetResult();
            try { if (reading is not null) _ = await Record.ExceptionAsync(() => reading.WaitAsync(Bound)); }
            finally
            {
                try { if (events is not null) _ = await Record.ExceptionAsync(() => events.DisposeAsync().AsTask().WaitAsync(Bound)); }
                finally
                {
                    try { _ = await Record.ExceptionAsync(() => retirement.WaitAsync(Bound)); }
                    finally { _ = await Record.ExceptionAsync(() => client.DisposeAsync().AsTask().WaitAsync(Bound)); }
                }
            }
        }
        static async IAsyncEnumerable<string> Input() { yield return "actual"; await Task.CompletedTask; }
    }

    [Fact]
    public async Task ActiveResourceFailureAndIndependentCleanupCausesAppearOnce()
    {
        using var access = new LlmConnectionAccess(new XaiTtsTests.Source(), []);
        using var http = new HttpClient(new XaiTtsTests.Handler(_ => throw new InvalidOperationException()));
        var commit = new InferenceResourceOperationException(Darkspyre.Inference.Common.InferenceFailureCode.ExecutionFailed, "actual commit refusal",
            new(InferenceResourceOperationPhase.Commit, new("workspace", "audio"), InferenceResourceCommitOutcome.Unknown, 1) { SelectionOutcome = InferenceResourceCommitOutcome.Unknown });
        var handleCleanup = new InvalidOperationException("actual handle cleanup refusal");
        var socketCleanup = new InvalidOperationException("actual socket cleanup refusal");
        var resources = new VoiceResourceFixture { CommitFailure = commit, CleanupFailure = handleCleanup };
        var socket = new XaiTtsStreamingTests.Socket { CleanupFailure = socketCleanup };
        socket.OnSend = value =>
        {
            if (!value.Contains("text.done", StringComparison.Ordinal)) return;
            socket.Push("{\"type\":\"audio.delta\",\"delta\":\"AQ==\"}");
        };
        ITtsClient client = XaiTtsStreamingTests.Provider(http, (_, _, _) => Task.FromResult<WebSocket>(socket)).Bind(XaiTtsTests.Connection, new(new("selected"), access), resources);
        var events = Assert.IsAssignableFrom<ITtsStreamingClient>(client).SpeakStreamAsync(Input(), new(Language: "en"), Limits).GetAsyncEnumerator();
        try
        {
            Assert.True(await events.MoveNextAsync().AsTask().WaitAsync(Bound)); Assert.IsType<TtsAudioChunk>(events.Current);
            socket.Push("{\"type\":\"audio.done\"}");
            Exception? error = await Record.ExceptionAsync(() => events.MoveNextAsync().AsTask().WaitAsync(Bound));
            Assert.NotNull(error);
            Assert.Equal(1, Count(error, commit)); Assert.Equal(1, Count(error, handleCleanup)); Assert.Equal(1, Count(error, socketCleanup));
            var aggregate = Assert.IsType<AggregateException>(error); Assert.Equal(2, aggregate.InnerExceptions.Count);
            var original = Assert.IsType<AggregateException>(aggregate.InnerExceptions[0]); Assert.Same(commit, original.InnerExceptions[0]);
            Assert.Same(error, await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()));
            Assert.Equal(1, resources.CommitCalls); Assert.Equal(1, resources.HandleDisposals); Assert.Equal(1, socket.Disposals); Assert.Equal(0, resources.BorrowedDisposals);
        }
        finally
        {
            try { _ = await Record.ExceptionAsync(() => events.DisposeAsync().AsTask()); }
            finally { _ = await Record.ExceptionAsync(() => client.DisposeAsync().AsTask()); }
        }
        static async IAsyncEnumerable<string> Input() { yield return "actual"; await Task.CompletedTask; }
        static int Count(Exception tree, Exception target) => (ReferenceEquals(tree, target) ? 1 : 0)
            + (tree is AggregateException aggregate ? aggregate.InnerExceptions.Sum(error => Count(error, target))
                : tree.InnerException is { } inner ? Count(inner, target) : 0);
    }

    private sealed class HeldResources(bool losesAcknowledgment, Exception? cleanup) : IInferenceResourceAccess
    {
        internal VoiceResourceFixture Inner { get; } = new() { CleanupFailure = cleanup };
        private bool LosesAcknowledgment => losesAcknowledgment;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string WorkspaceId => Inner.WorkspaceId;
        public string SessionId => Inner.SessionId;
        public Task<InferenceResourceSelection> GetSelectionAsync(CancellationToken cancellationToken = default) => Inner.GetSelectionAsync(cancellationToken);
        public async Task<IInferenceResourceWriteHandle> OpenWriteAsync(InferenceResourceCreate resource, CancellationToken cancellationToken = default)
            => new Handle(this, await Inner.OpenWriteAsync(resource, cancellationToken));
        public Task<InferenceResourceDescriptor?> GetAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IInferenceResourceReadHandle> OpenReadAsync(InferenceResourceReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InferenceResourceSelection> SelectAsync(IReadOnlyList<InferenceResourceReference> references, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InferenceResourceSelection> ReplaceSelectionAsync(IReadOnlyList<InferenceResourceReference> references, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        private sealed class Handle(HeldResources owner, IInferenceResourceWriteHandle inner) : IInferenceResourceWriteHandle
        {
            public InferenceResourceReference Reference => inner.Reference;
            public Stream Content => inner.Content;
            public async Task<InferenceResourceCommitResult> CommitAsync(long bytesWritten, InferenceResourceCommitOptions? options = null, CancellationToken cancellationToken = default)
            {
                owner.Entered.TrySetResult(); await owner.Release.Task;
                InferenceResourceCommitResult receipt = await inner.CommitAsync(bytesWritten, options, CancellationToken.None);
                if (owner.LosesAcknowledgment) throw new OperationCanceledException(cancellationToken);
                return receipt;
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
