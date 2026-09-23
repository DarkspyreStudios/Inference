using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorModelLifetimeTests
{
    [Fact]
    public async Task RequestWithoutLoadedModelFailsWithoutLoading()
    {
        using var lifetime = new TensorModelLifetime();

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(
            () => lifetime.EnterExecutionAsync());

        Assert.Equal(InferenceFailureCode.ModelUnavailable, exception.Code);
    }

    [Fact]
    public async Task UnloadWithoutCancellationDrainsTheRequestAndAllowsAnotherLoad()
    {
        using var lifetime = new TensorModelLifetime();
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
        using var lifetime = new TensorModelLifetime();
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
}
