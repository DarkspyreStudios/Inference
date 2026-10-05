using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>
/// Coordinates one resident model with requests and host-directed lifecycle work. Accepted lifecycle
/// work runs in order on a background thread; each transition is published through
/// <see cref="EngineStatusChanged"/>.
/// </summary>
public sealed class TensorModelLifetime : IDisposable
{
    private readonly object _owner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _chain = new();
    private Task _tail = Task.CompletedTask;
    private bool _stopAccepted;
    private CancellationTokenSource? _requestAbort;
    private int _unloading;
    private TensorModelKey? _model;
    private TensorBackendKind? _backend;
    private TensorModelObservation _observation = new(null, null, TensorEngineState.Unloaded, DateTimeOffset.UtcNow);

    /// <param name="owner">The engine instance, passed as the sender of <see cref="EngineStatusChanged"/>.</param>
    public TensorModelLifetime(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public event EventHandler<TensorEngineStatus>? EngineStatusChanged;

    public TensorModelObservation Observation => Volatile.Read(ref _observation);

    /// <summary>
    /// Queues lifecycle work after any earlier accepted work and returns. The work runs on a background
    /// thread with <paramref name="cancellationToken"/>, which stays live until the work finishes. The
    /// lifetime publishes <paramref name="running"/> when the work starts, the status the work returns
    /// when it succeeds, and Failed with the exception when it throws. A teardown
    /// (<paramref name="teardown"/>) refuses new requests from the moment it is accepted, and
    /// cancelling its token, at the call or later, cancels active requests at once.
    /// </summary>
    public Task AcceptAsync(
        TensorEngineState running,
        TensorModelKey? model,
        TensorBackendKind? backend,
        Func<CancellationToken, Task<TensorEngineStatus>> work,
        CancellationToken cancellationToken,
        bool teardown = false,
        bool stops = false)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_chain)
        {
            if (_stopAccepted)
            {
                throw new ObjectDisposedException(_owner.GetType().Name, "The tensor engine has been stopped.");
            }

            _stopAccepted = stops;
            CancellationTokenRegistration urgency = teardown ? FenceRequests(cancellationToken) : default;
            Task previous = _tail;
            _tail = RunAfterAsync(previous, running, model, backend, work, urgency, cancellationToken);
        }

        return Task.CompletedTask;
    }

    /// <summary>Waits for accepted lifecycle work and preserves the latest work's failure.</summary>
    public Task WhenIdleAsync()
    {
        lock (_chain)
        {
            return _tail;
        }
    }

    public bool StopAccepted
    {
        get { lock (_chain) { return _stopAccepted; } }
    }

    public async Task<IDisposable> EnterLoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_requestAbort is not null)
        {
            _gate.Release();
            throw new InvalidOperationException("Unload the current model before loading another one.");
        }

        return new GateLease(this, reopenRequests: false);
    }

    public void MarkLoaded(TensorModelKey model, TensorBackendKind backend)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_requestAbort is not null)
        {
            throw new InvalidOperationException("A model is already loaded.");
        }

        Volatile.Write(ref _requestAbort, new CancellationTokenSource());
        _model = model;
        _backend = backend;
        Observe(Observation.State);
    }

    public async Task<ExecutionLease> EnterExecutionAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _unloading) != 0)
        {
            throw ModelUnavailable();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_requestAbort is null || Volatile.Read(ref _unloading) != 0)
        {
            _gate.Release();
            throw ModelUnavailable();
        }

        try
        {
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _requestAbort.Token);
            return new ExecutionLease(_gate, linked);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public async Task<IDisposable> EnterUnloadAsync(CancellationToken urgencyToken = default)
    {
        using CancellationTokenRegistration cancellation = FenceRequests(urgencyToken);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        return new GateLease(this, reopenRequests: true);
    }

    /// <summary>Refuses new requests and cancels active ones when <paramref name="urgencyToken"/> is cancelled.</summary>
    private CancellationTokenRegistration FenceRequests(CancellationToken urgencyToken)
    {
        Volatile.Write(ref _unloading, 1);
        CancellationTokenSource? requestAbort = Volatile.Read(ref _requestAbort);
        return requestAbort is null
            ? default
            : urgencyToken.Register(static state =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Another completed unload already released this model.
                }
            }, requestAbort);
    }

    public void MarkUnloaded()
    {
        _requestAbort?.Dispose();
        Volatile.Write(ref _requestAbort, null);
        _model = null;
        _backend = null;
        Observe(Observation.State);
    }

    public void Dispose()
    {
        _requestAbort?.Dispose();
        _gate.Dispose();
    }

    private async Task RunAfterAsync(
        Task previous,
        TensorEngineState running,
        TensorModelKey? model,
        TensorBackendKind? backend,
        Func<CancellationToken, Task<TensorEngineStatus>> work,
        CancellationTokenRegistration urgency,
        CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration held = urgency;
        // Earlier work has already published its own outcome.
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Publish(new(model, backend, running, DateTimeOffset.UtcNow));
        TensorEngineStatus outcome;
        try
        {
            // Engine work is synchronous native compute or file access; it never runs on the caller's thread.
            outcome = await Task.Run(() => work(cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Publish(new(model, backend, TensorEngineState.Failed, DateTimeOffset.UtcNow, exception));
            throw;
        }

        Publish(outcome);
    }

    private void Publish(TensorEngineStatus status)
    {
        Observe(status.State);
        if (EngineStatusChanged is not { } listeners)
        {
            return;
        }

        foreach (EventHandler<TensorEngineStatus> listener in listeners.GetInvocationList().Cast<EventHandler<TensorEngineStatus>>())
        {
            try
            {
                listener(_owner, status);
            }
            catch (Exception)
            {
                // A listener does not own the engine's lifecycle; its failure cannot change the outcome or
                // stop other listeners from hearing it.
            }
        }
    }

    private void Observe(TensorEngineState state) =>
        Volatile.Write(ref _observation, new(_model, _backend, state, DateTimeOffset.UtcNow));

    private static InferenceException ModelUnavailable() =>
        new(InferenceFailureCode.ModelUnavailable, "No tensor model is available for this request.");

    private sealed class GateLease(TensorModelLifetime owner, bool reopenRequests) : IDisposable
    {
        private TensorModelLifetime? _owner = owner;

        public void Dispose()
        {
            TensorModelLifetime? current = Interlocked.Exchange(ref _owner, null);
            if (current is null)
            {
                return;
            }

            if (reopenRequests)
            {
                Volatile.Write(ref current._unloading, 0);
            }

            current._gate.Release();
        }
    }

    public sealed class ExecutionLease(SemaphoreSlim gate, CancellationTokenSource linked) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public CancellationToken CancellationToken => linked.Token;

        public ValueTask DisposeAsync()
        {
            linked.Dispose();
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
