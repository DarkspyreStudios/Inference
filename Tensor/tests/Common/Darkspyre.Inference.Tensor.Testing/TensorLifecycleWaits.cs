using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Testing;

/// <summary>
/// Test helpers that accept a lifecycle request and wait for its outcome on
/// <see cref="ITensorModelInstance.EngineStatusChanged"/>. A Failed outcome rethrows its error.
/// </summary>
public static class TensorLifecycleWaits
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static Task StartAndWaitAsync(this ITensorModelInstance instance, CancellationToken cancellationToken = default) =>
        instance.WaitForAsync(() => Accepted(instance.StartAsync(cancellationToken)), TensorEngineState.Started);

    public static Task LoadAndWaitAsync(this ITensorModelInstance instance, TensorModelKey model, CancellationToken cancellationToken = default) =>
        instance.WaitForAsync(() => Accepted(instance.LoadAsync(model, cancellationToken)), TensorEngineState.Loaded);

    public static Task UnloadAndWaitAsync(this ITensorModelInstance instance, CancellationToken cancellationToken = default) =>
        instance.WaitForAsync(() => Accepted(instance.UnloadAsync(cancellationToken)), TensorEngineState.Unloaded);

    public static Task StopAndWaitAsync(this ITensorModelInstance instance, CancellationToken cancellationToken = default) =>
        instance.WaitForAsync(() => Accepted(instance.StopAsync(cancellationToken)), TensorEngineState.Stopped);

    /// <summary>
    /// Runs <paramref name="accept"/>, such as a settings action that starts a lifecycle request, then waits
    /// for <paramref name="target"/>. Returns the result of <paramref name="accept"/>.
    /// </summary>
    public static async Task<T> WaitForAsync<T>(this ITensorModelInstance instance, Func<Task<T>> accept, TensorEngineState target)
    {
        var outcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(object? sender, TensorEngineStatus status)
        {
            if (status.State == target)
            {
                outcome.TrySetResult();
            }
            else if (status.State == TensorEngineState.Failed)
            {
                outcome.TrySetException(status.Error ?? new InvalidOperationException("The lifecycle request failed."));
            }
        }

        instance.EngineStatusChanged += OnStatus;
        try
        {
            T result = await accept().ConfigureAwait(false);
            await outcome.Task.WaitAsync(Timeout).ConfigureAwait(false);
            return result;
        }
        finally
        {
            instance.EngineStatusChanged -= OnStatus;
        }
    }

    private static async Task<bool> Accepted(Task request)
    {
        await request.ConfigureAwait(false);
        return true;
    }
}
