namespace Darkspyre.Inference.Tensor.Common;

/// <summary>The lifecycle states an in-process tensor engine reports through <see cref="ITensorModelInstance.EngineStatusChanged"/>.</summary>
public enum TensorEngineState
{
    Starting,
    Started,
    Loading,
    Loaded,
    Unloading,
    Unloaded,
    Stopping,
    Stopped,
    Failed
}

/// <summary>
/// One lifecycle transition. <see cref="Model"/> and <see cref="Backend"/> name the model and backend
/// the transition concerns; <see cref="Error"/> is set only for <see cref="TensorEngineState.Failed"/>.
/// </summary>
public sealed record TensorEngineStatus(
    TensorModelKey? Model, TensorBackendKind? Backend, TensorEngineState State, DateTimeOffset At, Exception? Error = null);

/// <summary>The instance's resident model, its backend and its latest lifecycle state.</summary>
public sealed record TensorModelObservation(
    TensorModelKey? Model, TensorBackendKind? Backend, TensorEngineState State, DateTimeOffset ObservedAt);

/// <summary>An in-process tensor engine whose model lifetime is controlled by its host.</summary>
/// <remarks>
/// <see cref="StartAsync"/>, <see cref="LoadAsync"/>, <see cref="UnloadAsync"/> and <see cref="StopAsync"/>
/// validate their input and return once the engine accepts the request. The engine runs the work in
/// order on a background thread and reports its outcome through <see cref="EngineStatusChanged"/>.
/// <see cref="IAsyncDisposable.DisposeAsync"/> performs Stop and completes after native work has
/// released model memory.
/// </remarks>
public interface ITensorModelInstance : IAsyncDisposable
{
    /// <summary>
    /// Raised for each lifecycle transition. The instance calls each listener and moves on; a listener
    /// with async work posts it and returns. The instance catches a listener's exception.
    /// </summary>
    event EventHandler<TensorEngineStatus>? EngineStatusChanged;

    Task<TensorModelObservation> GetObservationAsync(CancellationToken cancellationToken = default);

    TensorBackendKind RequestedBackend { get; }

    /// <summary>Accepts initialization of resources this instance needs; reports Started.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts loading one registered, installed, supported model; reports Loaded or Failed. A model
    /// that is not registered, installed or supported fails on the call. The token stays live until the
    /// load finishes.
    /// </summary>
    Task LoadAsync(TensorModelKey model, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts releasing the loaded model; reports Unloaded. The token stays live until the teardown
    /// finishes: an uncancelled teardown waits for active requests, and cancelling it, at the call or
    /// later, cancels them. Native work still finishes before model memory is released.
    /// </summary>
    Task UnloadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts terminal shutdown, including unload; reports Stopped. The token has the same urgency
    /// semantics as <see cref="UnloadAsync"/>. The instance cannot be reused after Stop.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
