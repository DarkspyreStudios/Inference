using System.Threading.Channels;
using System.Reflection;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class AntigravitySessionLifetimeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private const string Conversation = "d2fb65d7-e4c7-484a-9565-09b2cd4551b2";
    private const string Init = "{\"event\":\"init\",\"conversation_id\":\"d2fb65d7-e4c7-484a-9565-09b2cd4551b2\",\"init\":{\"tools\":[]}}";

    [Fact]
    public async Task DisposalWaitsForAPausedDirectTurnAndClosesObservationImmediately()
    {
        var factory = new Factory();
        ILlmAgentSession session = await OpenAsync(factory);
        IAsyncEnumerator<LlmAgentEvent> turn = session.RunTurnAsync(Request()).GetAsyncEnumerator();
        await using IAsyncEnumerator<LlmAgentEvent> observation = session.ObserveAsync().GetAsyncEnumerator();
        Task<bool> observing = observation.MoveNextAsync().AsTask();
        try
        {
            Assert.True(await turn.MoveNextAsync());
            Task retirement = session.DisposeAsync().AsTask();
            Assert.Same(retirement, session.DisposeAsync().AsTask());
            Assert.False(await observing.WaitAsync(Bound));
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, factory.Processes[0].Disposals);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.MoveNextAsync().AsTask());
            await turn.DisposeAsync();
            await retirement.WaitAsync(Bound);
            Assert.Equal(1, factory.Processes[0].Disposals);
            Assert.Empty(factory.Processes[0].Writes);
        }
        finally
        {
            await turn.DisposeAsync();
            _ = await Record.ExceptionAsync(() => session.DisposeAsync().AsTask().WaitAsync(Bound));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementCancelsAndDrainsDirectPromptWrites(bool ignoreCancellation)
    {
        var factory = new Factory { HoldWrite = true, IgnoreWriteCancellation = ignoreCancellation };
        ILlmAgentSession session = await OpenAsync(factory);
        IAsyncEnumerator<LlmAgentEvent> turn = session.RunTurnAsync(Request()).GetAsyncEnumerator();
        Process process = factory.Processes[0];
        Task<bool>? writing = null;
        try
        {
            Assert.True(await turn.MoveNextAsync());
            writing = turn.MoveNextAsync().AsTask();
            await process.WriteEntered.Task.WaitAsync(Bound);
            Task retirement = session.DisposeAsync().AsTask();
            Assert.True(process.WriteToken.IsCancellationRequested);
            if (ignoreCancellation)
            {
                Assert.False(retirement.IsCompleted);
                Assert.Equal(0, process.Disposals);
                Assert.False(process.Killed);
                process.ReleaseWrite.TrySetResult();
            }
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(Bound));
            await turn.DisposeAsync();
            await retirement.WaitAsync(Bound);
            Assert.Equal(1, process.Disposals);
        }
        finally
        {
            process.ReleaseWrite.TrySetResult();
            if (writing is not null) _ = await Record.ExceptionAsync(() => writing.WaitAsync(Bound));
            await turn.DisposeAsync();
            _ = await Record.ExceptionAsync(() => session.DisposeAsync().AsTask().WaitAsync(Bound));
        }
    }

    [Fact]
    public async Task ReopenCannotStartAnotherProcessAfterRetirement()
    {
        var factory = new Factory { HoldExit = true };
        ILlmAgentSession session = await OpenAsync(factory);
        Process process = factory.Processes[0];
        IAsyncEnumerator<LlmAgentEvent> turn = session.RunTurnAsync(Request() with { ReasoningLevel = LlmReasoningLevel.High }).GetAsyncEnumerator();
        Task<bool> reopening = turn.MoveNextAsync().AsTask();
        try
        {
            await process.ExitEntered.Task.WaitAsync(Bound);
            Task retirement = session.DisposeAsync().AsTask();
            Assert.True(process.ExitToken.IsCancellationRequested);
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, process.Disposals);
            process.ReleaseExit.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reopening.WaitAsync(Bound));
            await turn.DisposeAsync();
            await retirement.WaitAsync(Bound);
            Assert.Single(factory.Processes);
            Assert.Equal(Conversation, session.ThreadId);
        }
        finally
        {
            process.ReleaseExit.TrySetResult();
            _ = await Record.ExceptionAsync(() => reopening.WaitAsync(Bound));
            await turn.DisposeAsync();
            _ = await Record.ExceptionAsync(() => session.DisposeAsync().AsTask().WaitAsync(Bound));
        }
    }

    [Fact]
    public async Task CancelledTurnRetainsItsCleanupFailureForEveryDisposer()
    {
        var failure = new CliProcessTerminationException(4242);
        var factory = new Factory { KillFailure = failure };
        ILlmAgentSession session = await OpenAsync(factory);
        using var cancellation = new CancellationTokenSource();
        await using IAsyncEnumerator<LlmAgentEvent> turn = session.RunTurnAsync(Request(), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await turn.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.MoveNextAsync().AsTask());
        InvalidOperationException fault = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(session.RunTurnAsync(Request())));
        Assert.Contains("4242", fault.Message, StringComparison.Ordinal);
        Task retirement = session.DisposeAsync().AsTask();
        Assert.Same(failure, await Record.ExceptionAsync(() => retirement.WaitAsync(Bound)));
        Assert.Same(retirement, session.DisposeAsync().AsTask());
        Assert.Equal(1, factory.Processes[0].Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementFencesRemainingMappedEventsAndLateUsage(bool ignoreReadCancellation)
    {
        var factory = new Factory { IgnoreReadCancellation = ignoreReadCancellation };
        ILlmAgentSession session = await OpenAsync(factory);
        await using IAsyncEnumerator<LlmAgentEvent> turn = session.RunTurnAsync(Request()).GetAsyncEnumerator();
        Assert.True(await turn.MoveNextAsync());
        Process process = factory.Processes[0];
        process.Emit("{\"event\":\"step_update\",\"step_update\":{\"step_index\":0,\"state\":\"DONE\",\"step_type\":\"user_input\"}}");
        process.Emit("{\"event\":\"step_update\",\"step_update\":{\"step_index\":1,\"state\":\"ACTIVE\",\"step_type\":\"agent_response\",\"text_delta\":\"late text\"}}");
        while (await turn.MoveNextAsync().AsTask().WaitAsync(Bound) && turn.Current is not LlmAgentActivityEvent) { }
        Task retirement = session.DisposeAsync().AsTask();
        process.Emit("{\"event\":\"step_update\",\"step_update\":{\"step_index\":1,\"state\":\"DONE\",\"step_type\":\"agent_response\",\"usage\":{\"input_tokens\":9,\"output_tokens\":2,\"total_tokens\":11}}}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.MoveNextAsync().AsTask());
        await turn.DisposeAsync();
        await retirement.WaitAsync(Bound);
        var accounted = (System.Collections.IEnumerable)typeof(AntigravityAgentSession)
            .GetField("_accountedSteps", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        Assert.Equal((1, 0), Assert.Single(accounted.Cast<object>()));
        Assert.Equal(Conversation, session.ThreadId);
    }

    [Fact]
    public async Task RetirementDuringReopenHandshakeDoesNotPublishOrSendATurn()
    {
        var factory = new Factory { HoldReopenHandshake = true };
        ILlmAgentSession session = await OpenAsync(factory);
        await using IAsyncEnumerator<LlmAgentEvent> turn = session.RunTurnAsync(Request() with { ReasoningLevel = LlmReasoningLevel.High }).GetAsyncEnumerator();
        Task<bool> reopening = turn.MoveNextAsync().AsTask();
        await factory.Reopened.Task.WaitAsync(Bound);
        Task retirement = session.DisposeAsync().AsTask();
        factory.Processes[1].Emit(Init);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reopening.WaitAsync(Bound));
        await turn.DisposeAsync();
        await retirement.WaitAsync(Bound);
        Assert.Equal(2, factory.Processes.Count);
        Assert.All(factory.Processes, process => { Assert.Equal(1, process.Disposals); Assert.Empty(process.Writes); });
        Assert.Equal(Conversation, session.ThreadId);
    }

    [Fact]
    public async Task ReopenCleanupFailurePreventsReplacementAndRemainsVisibleToDisposal()
    {
        var failure = new CliProcessTerminationException(4242);
        var factory = new Factory { KillFailure = failure };
        ILlmAgentSession session = await OpenAsync(factory);
        await using IAsyncEnumerator<LlmAgentEvent> turn = session.RunTurnAsync(Request() with { ReasoningLevel = LlmReasoningLevel.High }).GetAsyncEnumerator();
        Assert.Same(failure, await Record.ExceptionAsync(() => turn.MoveNextAsync().AsTask()));
        Assert.Single(factory.Processes);
        Task retirement = session.DisposeAsync().AsTask();
        Assert.Same(failure, await Record.ExceptionAsync(() => retirement.WaitAsync(Bound)));
        Assert.Same(retirement, session.DisposeAsync().AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledTurnPreservesEveryFailedCleanupStage(bool readerFailure)
    {
        var kill = new CliProcessTerminationException(4242);
        var second = new IOException("controlled cleanup failure");
        var factory = new Factory { KillFailure = kill, DisposeFailure = readerFailure ? null : second, ReadFailureOnClose = readerFailure ? second : null };
        ILlmAgentSession session = await OpenAsync(factory);
        using var cancellation = new CancellationTokenSource();
        await using IAsyncEnumerator<LlmAgentEvent> turn = session.RunTurnAsync(Request(), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await turn.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn.MoveNextAsync().AsTask());
        Task retirement = session.DisposeAsync().AsTask();
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => retirement.WaitAsync(Bound));
        Assert.Collection(failure.InnerExceptions, error => Assert.Same(kill, error), error => Assert.Same(second, error));
        Assert.Same(failure, await Record.ExceptionAsync(() => session.DisposeAsync().AsTask()));
        Assert.Equal(1, factory.Processes[0].Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOpeningPreservesItsOriginalFailureAndCleanupFailure(bool cancelled)
    {
        var cleanup = new IOException("controlled cleanup failure");
        var factory = new Factory { InvalidHandshake = !cancelled, HoldInitialHandshake = cancelled, DisposeFailure = cleanup };
        using var cancellation = new CancellationTokenSource();
        Task<ILlmAgentSession> opening = OpenAsync(factory, cancellation.Token);
        if (cancelled)
        {
            await factory.Started.Task.WaitAsync(Bound);
            cancellation.Cancel();
        }
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => opening.WaitAsync(Bound));
        Assert.Collection(failure.InnerExceptions,
            error => { if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(error); else Assert.IsType<LlmProviderException>(error); },
            error => Assert.Same(cleanup, error));
        Assert.Equal(1, factory.Processes[0].Disposals);
    }

    private static LlmAgentTurnRequest Request() => new() { Input = [new LlmAgentTextContent("hello")] };

    private static Task<ILlmAgentSession> OpenAsync(Factory factory, CancellationToken cancellationToken = default) => AntigravityAgentSession.OpenAsync(factory,
        "agy", Environment.CurrentDirectory, new("gemini-pro", null, null), null,
        new() { Version = "1", Transport = LlmTransport.GoogleAntigravityCli, Endpoint = "agy" }, cancellationToken);

    private static async Task ReadAsync(IAsyncEnumerable<LlmAgentEvent> events)
    {
        await foreach (LlmAgentEvent _ in events) { }
    }

    private sealed class Factory : IAntigravityCliSessionProcessFactory
    {
        internal bool HoldWrite { get; init; }
        internal bool IgnoreWriteCancellation { get; init; }
        internal bool HoldExit { get; init; }
        internal bool HoldReopenHandshake { get; init; }
        internal bool InvalidHandshake { get; init; }
        internal bool HoldInitialHandshake { get; init; }
        internal bool IgnoreReadCancellation { get; init; }
        internal Exception? KillFailure { get; init; }
        internal Exception? DisposeFailure { get; init; }
        internal Exception? ReadFailureOnClose { get; init; }
        internal TaskCompletionSource Reopened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<Process> Processes { get; } = [];

        public IAntigravityCliSessionProcess Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
        {
            var process = new Process(this);
            Processes.Add(process);
            if ((Processes.Count == 1 && !HoldInitialHandshake) || (Processes.Count > 1 && !HoldReopenHandshake))
                process.Emit(InvalidHandshake ? "{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\"}}" : Init);
            Started.TrySetResult();
            if (Processes.Count == 2) Reopened.TrySetResult();
            return process;
        }
    }

    private sealed class Process(Factory factory) : IAntigravityCliSessionProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        internal TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ExitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseExit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken WriteToken { get; private set; }
        internal CancellationToken ExitToken { get; private set; }
        internal List<string> Writes { get; } = [];
        internal int Disposals { get; private set; }
        internal bool Killed { get; private set; }

        internal void Emit(string text) => _output.Writer.TryWrite(text);

        public async ValueTask WriteAsync(string text, CancellationToken cancellationToken = default)
        {
            WriteToken = cancellationToken;
            WriteEntered.TrySetResult();
            if (factory.HoldWrite)
                await ReleaseWrite.Task.WaitAsync(factory.IgnoreWriteCancellation ? CancellationToken.None : cancellationToken);
            Writes.Add(text);
        }

        public void CloseInput() => _output.Writer.TryComplete();

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            try { return await _output.Reader.ReadAsync(factory.IgnoreReadCancellation ? CancellationToken.None : cancellationToken); }
            catch (ChannelClosedException)
            {
                if (factory.ReadFailureOnClose is { } failure) throw failure;
                return null;
            }
        }

        public async Task<(int ExitCode, string StandardError)> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            ExitToken = cancellationToken;
            ExitEntered.TrySetResult();
            if (factory.HoldExit) await ReleaseExit.Task;
            return (0, string.Empty);
        }

        public Task KillAsync()
        {
            Killed = true;
            CloseInput();
            return factory.KillFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            CloseInput();
            return factory.DisposeFailure is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
        }
    }
}
