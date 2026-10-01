using System.Runtime.CompilerServices;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class HostedManagedLifetimeTests
{
    [Fact]
    public async Task CancellationFailureStillDrainsProducerAndAllDisposersShareFailure()
    {
        var harness = new Harness();
        var session = new ManagedAgentSession(new() { Transport = LlmTransport.OpenAiResponses, Endpoint = "https://example.test", Version = "v" },
            new() { ModelId = "model" }, [], harness, harness.Stream, _ => new()
            { SupportsAgentSessions = true, Agent = ManagedAgentPolicy.Capabilities });
        Task turn = Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("hello")] }));
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
            Assert.Same(harness.Failure, Assert.Single(failure.InnerExceptions));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
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

    private static async Task Read(IAsyncEnumerable<LlmAgentEvent> stream)
    { await foreach (var unused in stream) { } }

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
