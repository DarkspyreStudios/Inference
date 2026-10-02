using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedManagedLifetimeTests
{
    [Fact]
    public async Task InterruptCancellationFailureStillDrainsIgnoringProducer()
    {
        var harness = new Harness();
        var session = await ManagedAgentSession.CreateAsync(new() { Transport = LlmTransport.OpenAiResponses, Endpoint = "https://example.test", Version = "v" },
            new() { ModelId = "model" }, [], harness, harness.Stream, _ => new()
            { SupportsAgentSessions = true, Agent = ManagedAgentPolicy.Capabilities });
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<LlmAgentEvent>> turn = ReadStarted(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")] }), started);
        Task? interruption = null;
        Task? retirement = null;
        try
        {
            string turnId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            interruption = session.InterruptAsync(turnId);
            await harness.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<TimeoutException>(() => interruption.WaitAsync(TimeSpan.FromMilliseconds(200)));
            harness.Release.TrySetResult();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => interruption);
            Assert.Same(harness.Failure, Assert.Single(failure.Flatten().InnerExceptions));
            Assert.Equal(LlmAgentCompletionReason.Interruption, Assert.Single((await turn).OfType<LlmAgentTurnCompletedEvent>()).Reason);
            await session.DisposeAsync();
        }
        finally
        {
            retirement ??= session.DisposeAsync().AsTask();
            harness.Release.TrySetResult();
            if (interruption is not null) await Drain(interruption);
            await Drain(turn);
            await Drain(retirement);
        }
    }

    [Fact]
    public async Task AbandonedIteratorCancellationFailureStillDrainsIgnoringProducer()
    {
        var harness = new Harness();
        var session = await ManagedAgentSession.CreateAsync(new() { Transport = LlmTransport.OpenAiResponses, Endpoint = "https://example.test", Version = "v" },
            new() { ModelId = "model" }, [], harness, harness.Stream, _ => new()
            { SupportsAgentSessions = true, Agent = ManagedAgentPolicy.Capabilities });
        IAsyncEnumerator<LlmAgentEvent> iterator = session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")] }).GetAsyncEnumerator();
        Task? abandoned = null;
        Task? retirement = null;
        try
        {
            Assert.True(await iterator.MoveNextAsync());
            await harness.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            abandoned = iterator.DisposeAsync().AsTask();
            await harness.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(abandoned.IsCompleted);
            retirement = session.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            harness.Release.TrySetResult();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => abandoned);
            Assert.Same(harness.Failure, Assert.Single(failure.Flatten().InnerExceptions));
            await retirement;
            await session.DisposeAsync();
        }
        finally
        {
            retirement ??= session.DisposeAsync().AsTask();
            harness.Release.TrySetResult();
            abandoned ??= iterator.DisposeAsync().AsTask();
            await Drain(abandoned);
            await Drain(retirement);
        }
    }

    [Fact]
    public async Task CancellationFailureStillDrainsProducerAndAllDisposersShareFailure()
    {
        var harness = new Harness();
        var session = await ManagedAgentSession.CreateAsync(new() { Transport = LlmTransport.OpenAiResponses, Endpoint = "https://example.test", Version = "v" },
            new() { ModelId = "model" }, [], harness, harness.Stream, _ => new()
            { SupportsAgentSessions = true, Agent = ManagedAgentPolicy.Capabilities });
        Task<IReadOnlyList<LlmAgentEvent>> turn = Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")] }));
        Task? first = null;
        Task? second = null;
        try
        {
            await harness.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            first = session.DisposeAsync().AsTask();
            second = session.DisposeAsync().AsTask();
            await harness.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            harness.Release.TrySetResult();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => first);
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => second));
            Assert.Same(harness.Failure, Assert.Single(failure.Flatten().InnerExceptions));
            Assert.Equal(LlmAgentCompletionReason.Interruption, Assert.Single((await turn).OfType<LlmAgentTurnCompletedEvent>()).Reason);
            Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => session.DisposeAsync().AsTask()));
        }
        finally
        {
            first ??= session.DisposeAsync().AsTask();
            harness.Release.TrySetResult();
            await Drain(turn);
            await Drain(first);
            if (second is not null) await Drain(second);
        }
    }

    private static async Task<IReadOnlyList<LlmAgentEvent>> Read(IAsyncEnumerable<LlmAgentEvent> stream)
    {
        List<LlmAgentEvent> events = [];
        await foreach (var item in stream) { events.Add(item); }
        return events;
    }

    private static async Task<IReadOnlyList<LlmAgentEvent>> ReadStarted(IAsyncEnumerable<LlmAgentEvent> stream,
        TaskCompletionSource<string> started)
    {
        List<LlmAgentEvent> events = [];
        await foreach (var item in stream)
        {
            if (item is LlmAgentTurnStartedEvent turn) started.TrySetResult(turn.TurnId);
            events.Add(item);
        }
        return events;
    }

    private static async Task Drain(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception) when (task.IsCompleted) { }
    }

    private sealed class Harness : ILlmContextEstimator
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal InvalidOperationException Failure { get; } = new("controlled callback failure");
        public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request) => new(1);
        public string? EstimateDiagnostic => null;
        internal async IAsyncEnumerable<LlmStreamEvent> Stream(LlmChatRequest request,
            [EnumeratorCancellation] CancellationToken token)
        {
            using CancellationTokenRegistration registration = token.Register(() =>
            { Canceled.TrySetResult(); throw Failure; });
            Entered.TrySetResult();
            await Release.Task;
            token.ThrowIfCancellationRequested();
            yield return new LlmChatCompletionEvent(new() { Text = "done", Status = LlmCompletionStatus.Completed, Diagnostics = new(200, null, null) });
        }
    }
}
