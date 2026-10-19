using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorModelLifetimeTests
{
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
}
