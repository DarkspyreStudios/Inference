using Runner = Darkspyre.Inference.Tensor.ConsoleTester.Program;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class ConsoleRuntimeLifetimeTests
{
    [Fact]
    public async Task OperationAndIndependentCleanupFailuresRemainTogetherWithoutRetirementRetry()
    {
        var operation = new InvalidOperationException("operation failed");
        var engineError = new IOException("engine release failed");
        var runtimeError = new InvalidOperationException("supplier retirement failed");
        var phases = new List<string>();
        var engine = new Engine(phases, engineError);
        var runtime = new Runtime(phases, runtimeError);

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => Runner.RunOwnedEngineAsync(
            () => { phases.Add("run"); return Task.FromException<int>(operation); }, engine, runtime));

        Assert.Equal([operation, engineError, runtimeError], failure.InnerExceptions);
        Assert.Equal(["run", "engine", "runtime"], phases);
    }

    private sealed class Engine(List<string> phases, Exception? failure = null) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            phases.Add("engine");
            return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
        }
    }

    private sealed class Runtime(List<string> phases, Exception? failure = null) : ITensorRuntimeOwner
    {
        public TensorRuntimePlan Plan => throw new NotSupportedException();
        public TensorRuntimeState State => TensorRuntimeState.Unavailable;
        public TensorRuntimeBackendIdentity? Current => null;

        public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            phases.Add("runtime");
            return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
        }
    }
}
