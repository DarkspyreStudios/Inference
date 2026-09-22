namespace Darkspyre.Inference.Tensor.Common;

/// <summary>An in-process tensor engine whose model lifetime is controlled by its host.</summary>
public interface ITensorModelInstance : IAsyncDisposable
{
    /// <summary>Initializes resources needed for this instance; may be a no-op.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads one previously installed, supported model into this instance.</summary>
    Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for active requests and releases the loaded model. Cancelling the token asks active
    /// requests to stop promptly, but native work must finish before model memory is released.
    /// </summary>
    Task UnloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs terminal instance shutdown, including unload. Cancellation has the same urgency
    /// semantics as <see cref="UnloadAsync"/>; the instance cannot be reused after Stop.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
