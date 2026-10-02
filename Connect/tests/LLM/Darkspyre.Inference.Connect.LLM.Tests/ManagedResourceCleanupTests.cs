using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class ManagedResourceCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualResponseCleanupRefusalIsAnOwnedResourceFailure(bool malformedBody)
    {
        var resources = new ControlledSessionResources();
        await resources.RegisterAsync("source.txt", "text/plain", "source");
        var cleanup = new InvalidOperationException("actual response disposal refused");
        using var destination = new LlmResourceMaterializationTests.ResourceDestination { ResponseCleanupFailure = cleanup };
        if (malformedBody) destination.Responses.Enqueue("not-json");
        using var http = new HttpClient(destination);
        var client = new XaiResponsesLlmClient(http);
        var session = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(),
            new() { ModelId = "grok-4", CallerServices = new() { Resources = resources } }, [], client,
            (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token),
            model => ManagedResourceMaterializationTests.ManagedCapabilities(client, model));
        Task? retirement = null;
        try
        {
            Exception readerError = await Assert.ThrowsAnyAsync<Exception>(() => ManagedResourceMaterializationTests.Read(
                session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("inspect")] })));
            var carrier = Assert.Single(Leaves(readerError).OfType<InferenceResourceOperationException>(),
                failure => failure.Failure.Phase == (InferenceResourceOperationPhase)9);
            Assert.Same(cleanup, carrier.InnerException);
            Assert.Null(carrier.Failure.Reference);
            Assert.Equal(malformedBody ? 2 : 1, Leaves(readerError).Count());
            retirement = session.DisposeAsync().AsTask();
            Assert.Same(readerError, await Assert.ThrowsAnyAsync<Exception>(() => retirement));
            Assert.Equal(1, destination.ResponseCleanupAttempts);
            Assert.Equal(1, resources.DisposedReads);
        }
        finally { retirement ??= session.DisposeAsync().AsTask(); await Drain(retirement); }
    }

    [Fact]
    public async Task ExplicitFaultCompletionKeepsCleanupCancellationInFaultAccounting()
    {
        var original = new OperationCanceledException("actual cleanup refusal");
        Task scheduled = Task.Run(async () => { await Task.Yield(); throw original; });
        Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => scheduled));
        Assert.True(scheduled.IsCanceled);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetException(original);
        Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => completion.Task));
        Assert.True(completion.Task.IsFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualHandleCleanupCancellationPreservesSameReaderAndRetirementFailure(bool cancelRead)
    {
        var resources = new ControlledSessionResources();
        InferenceResourceReference reference = await resources.RegisterAsync("source.txt", "text/plain", "source");
        var cleanup = new OperationCanceledException("actual handle cleanup refused");
        resources.ReadCleanupFailure = cleanup;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancelRead) resources.BeforeRead = async token =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            entered.TrySetResult();
            await release.Task;
        };
        using var destination = new LlmResourceMaterializationTests.ResourceDestination();
        using var http = new HttpClient(destination);
        var client = new XaiResponsesLlmClient(http);
        var session = await ManagedAgentSession.CreateAsync(LlmResourceMaterializationTests.Connection(),
            new() { ModelId = "grok-4", CallerServices = new() { Resources = resources } }, [], client,
            (request, token) => client.StreamAsync(LlmResourceMaterializationTests.Connection(), request, token),
            model => ManagedResourceMaterializationTests.ManagedCapabilities(client, model));
        Task<List<LlmAgentEvent>> turn = ManagedResourceMaterializationTests.Read(session.RunTurnAsync(new() { Input = [new LlmAgentTextContent("inspect")] }));
        Task? retirement = null;
        try
        {
            if (cancelRead)
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                retirement = session.DisposeAsync().AsTask();
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(retirement.IsCompleted);
                release.TrySetResult();
            }
            Exception readerError = await Assert.ThrowsAnyAsync<Exception>(() => turn);
            InferenceResourceOperationCanceledException cleanupCarrier = Assert.Single(Leaves(readerError)
                .OfType<InferenceResourceOperationCanceledException>(), error => error.Failure.Phase == (InferenceResourceOperationPhase)9);
            Assert.Same(cleanup, cleanupCarrier.InnerException);
            Assert.Equal(reference, cleanupCarrier.Failure.Reference);
            Assert.Equal(cancelRead ? 2 : 1, Leaves(readerError).Count());
            retirement ??= session.DisposeAsync().AsTask();
            Assert.Same(readerError, await Assert.ThrowsAnyAsync<Exception>(() => retirement));
            Assert.Same(readerError, await Assert.ThrowsAnyAsync<Exception>(() => session.DisposeAsync().AsTask()));
            Assert.Equal(1, resources.ReadCleanupAttempts);
            Assert.Equal(0, resources.DisposedReads);
            Assert.Equal(0, resources.DisposeCalls);
        }
        finally
        {
            retirement ??= session.DisposeAsync().AsTask();
            release.TrySetResult();
            await Drain(turn);
            await Drain(retirement);
        }
    }

    private static IEnumerable<Exception> Leaves(Exception error) => error is AggregateException aggregate
        ? aggregate.InnerExceptions.SelectMany(Leaves) : [error];
    private static async Task Drain(Task task)
    { try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) when (task.IsCompleted) { } }
}
