using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorModelLifetimeTests
{
    [Fact]
    public async Task RequestWithoutLoadedModelFailsWithoutLoading()
    {
        using var lifetime = new TensorModelLifetime(new object());

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(
            () => lifetime.EnterExecutionAsync());

        Assert.Equal(InferenceFailureCode.ModelUnavailable, exception.Code);
    }

    [Fact]
    public async Task UnloadWithoutCancellationDrainsTheRequestAndAllowsAnotherLoad()
    {
        using var lifetime = new TensorModelLifetime(new object());
        using (await lifetime.EnterLoadAsync())
        {
            lifetime.MarkLoaded(new("model", "revision"), TensorBackendKind.Cpu);
        }

        TensorModelLifetime.ExecutionLease execution = await lifetime.EnterExecutionAsync();
        Task<IDisposable> unloading = lifetime.EnterUnloadAsync();

        Assert.False(unloading.IsCompleted);
        Assert.False(execution.CancellationToken.IsCancellationRequested);

        await execution.DisposeAsync();
        using (await unloading.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            lifetime.MarkUnloaded();
        }

        using (await lifetime.EnterLoadAsync())
        {
            lifetime.MarkLoaded(new("model", "revision"), TensorBackendKind.Cpu);
        }

        using (await lifetime.EnterUnloadAsync())
        {
            lifetime.MarkUnloaded();
        }
    }

    [Fact]
    public async Task CancelledUnloadAbortsRequestButWaitsForItsSafeExit()
    {
        using var lifetime = new TensorModelLifetime(new object());
        using (await lifetime.EnterLoadAsync())
        {
            lifetime.MarkLoaded(new("model", "revision"), TensorBackendKind.Cpu);
        }

        TensorModelLifetime.ExecutionLease execution = await lifetime.EnterExecutionAsync();
        using var urgency = new CancellationTokenSource();
        Task<IDisposable> unloading = lifetime.EnterUnloadAsync(urgency.Token);
        urgency.Cancel();

        Assert.True(execution.CancellationToken.IsCancellationRequested);
        Assert.False(unloading.IsCompleted);

        await execution.DisposeAsync();
        using (await unloading.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            lifetime.MarkUnloaded();
        }
    }

    [Fact]
    public async Task AcceptReturnsBeforeTheWorkFinishesAndPublishesItsOutcomeInOrder()
    {
        object owner = new();
        using var lifetime = new TensorModelLifetime(owner);
        var model = new TensorModelKey("model", "revision");
        var statuses = new List<TensorEngineStatus>();
        object? sender = null;
        lifetime.EngineStatusChanged += (source, status) => { lock (statuses) { sender = source; statuses.Add(status); } };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task accepted = lifetime.AcceptAsync(TensorEngineState.Loading, model, TensorBackendKind.Cpu, async _ =>
        {
            await release.Task;
            return new TensorEngineStatus(model, TensorBackendKind.Cpu, TensorEngineState.Loaded, DateTimeOffset.UtcNow);
        }, CancellationToken.None);

        Assert.True(accepted.IsCompletedSuccessfully);
        release.SetResult();
        await lifetime.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(owner, sender);
        Assert.Equal([TensorEngineState.Loading, TensorEngineState.Loaded], statuses.Select(status => status.State));
        Assert.Equal(TensorEngineState.Loaded, lifetime.Observation.State);
    }

    [Fact]
    public async Task FailedWorkPublishesItsErrorAndAThrowingListenerCannotStopTheOthers()
    {
        using var lifetime = new TensorModelLifetime(new object());
        var failure = new InvalidDataException("corrupt");
        TensorEngineStatus? observed = null;
        lifetime.EngineStatusChanged += (_, _) => throw new InvalidOperationException("listener failure");
        lifetime.EngineStatusChanged += (_, status) => observed = status;

        await lifetime.AcceptAsync(TensorEngineState.Loading, null, null,
            _ => Task.FromException<TensorEngineStatus>(failure), CancellationToken.None);
        await lifetime.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TensorEngineState.Failed, observed!.State);
        Assert.Same(failure, observed.Error);
    }

    [Fact]
    public async Task AcceptedWorkRunsInOrderAndStopRefusesLaterRequests()
    {
        using var lifetime = new TensorModelLifetime(new object());
        var order = new List<string>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await lifetime.AcceptAsync(TensorEngineState.Loading, null, null, async _ =>
        {
            await first.Task;
            lock (order) { order.Add("load"); }
            return new TensorEngineStatus(null, null, TensorEngineState.Loaded, DateTimeOffset.UtcNow);
        }, CancellationToken.None);
        await lifetime.AcceptAsync(TensorEngineState.Stopping, null, null, _ =>
        {
            lock (order) { order.Add("stop"); }
            return Task.FromResult(new TensorEngineStatus(null, null, TensorEngineState.Stopped, DateTimeOffset.UtcNow));
        }, CancellationToken.None, teardown: true, stops: true);

        Assert.True(lifetime.StopAccepted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lifetime.AcceptAsync(TensorEngineState.Starting, null, null,
            _ => Task.FromResult(new TensorEngineStatus(null, null, TensorEngineState.Started, DateTimeOffset.UtcNow)),
            CancellationToken.None));
        first.SetResult();
        await lifetime.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["load", "stop"], order);
        Assert.Equal(TensorEngineState.Stopped, lifetime.Observation.State);
    }

    [Fact]
    public async Task AnAcceptedTeardownRefusesNewRequestsAndItsLaterCancellationAbortsActiveOnes()
    {
        using var lifetime = new TensorModelLifetime(new object());
        using (await lifetime.EnterLoadAsync())
        {
            lifetime.MarkLoaded(new("model", "revision"), TensorBackendKind.Cpu);
        }

        TensorModelLifetime.ExecutionLease execution = await lifetime.EnterExecutionAsync();
        using var urgency = new CancellationTokenSource();
        await lifetime.AcceptAsync(TensorEngineState.Unloading, null, null, async token =>
        {
            using (await lifetime.EnterUnloadAsync(token))
            {
                lifetime.MarkUnloaded();
            }

            return new TensorEngineStatus(null, null, TensorEngineState.Unloaded, DateTimeOffset.UtcNow);
        }, urgency.Token, teardown: true);

        await Assert.ThrowsAsync<InferenceException>(() => lifetime.EnterExecutionAsync());
        Assert.False(execution.CancellationToken.IsCancellationRequested);
        await urgency.CancelAsync();
        Assert.True(execution.CancellationToken.IsCancellationRequested);
        await execution.DisposeAsync();
        await lifetime.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TensorEngineState.Unloaded, lifetime.Observation.State);
    }
}
