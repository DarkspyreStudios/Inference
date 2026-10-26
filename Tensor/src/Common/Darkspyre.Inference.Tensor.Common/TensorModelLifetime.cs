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
    private readonly RequestGate _gate = new();
    private readonly Lock _chain = new();
    private Task _tail = Task.CompletedTask;
    private bool _stopAccepted;
    private CancellationTokenSource? _requestAbort;
    private int _unloading;
    private TensorModelKey? _model;
    private TensorBackendKind? _backend;
    private TensorExpertPlacement? _expertPlacement;
    private TensorModelObservation _observation = new(null, null, TensorEngineState.Unloaded, DateTimeOffset.UtcNow);

    /// <summary>Creates a lifetime with no resident model, in the Unloaded state.</summary>
    /// <param name="owner">The engine instance, passed as the sender of <see cref="EngineStatusChanged"/>.</param>
    public TensorModelLifetime(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <summary>Raised for each lifecycle transition, with the owner as sender. A listener's exception is caught and ignored.</summary>
    public event EventHandler<TensorEngineStatus>? EngineStatusChanged;

    /// <summary>The resident model, its backend and the latest lifecycle state.</summary>
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

    /// <summary>True once Stop work has been accepted; later lifecycle work is refused.</summary>
    public bool StopAccepted
    {
        get { lock (_chain) { return _stopAccepted; } }
    }

    /// <summary>Takes the request gate for a load and returns a lease that releases it. Throws when a model is already loaded.</summary>
    public async Task<IDisposable> EnterLoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(urgent: false, cancellationToken).ConfigureAwait(false);
        if (_requestAbort is not null)
        {
            _gate.Release();
            throw new InvalidOperationException("Unload the current model before loading another one.");
        }

        return new GateLease(this, reopenRequests: false);
    }

    /// <summary>Records <paramref name="model"/> on <paramref name="backend"/> as resident, with its expert placement when the engine reports one, and opens it to requests. Throws when a model is already loaded.</summary>
    public void MarkLoaded(TensorModelKey model, TensorBackendKind backend, TensorExpertPlacement? expertPlacement = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (_requestAbort is not null)
        {
            throw new InvalidOperationException("A model is already loaded.");
        }

        Volatile.Write(ref _requestAbort, new CancellationTokenSource());
        _model = model;
        _backend = backend;
        _expertPlacement = expertPlacement;
        Observe(Observation.State);
    }

    /// <summary>
    /// Takes the request gate for one request against the resident model. An <paramref name="urgent"/> request takes the gate
    /// before every waiting request that is not urgent, and before waiting load and unload work. Throws a model-unavailable
    /// error when no model is loaded or an unload is in progress.
    /// </summary>
    /// <param name="urgent">True for a request a person waits on, such as a search query.</param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the request.</param>
    public async Task<ExecutionLease> EnterExecutionAsync(bool urgent, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _unloading) != 0)
        {
            throw ModelUnavailable();
        }

        await _gate.WaitAsync(urgent, cancellationToken).ConfigureAwait(false);
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
            return new ExecutionLease(_gate, linked, urgent);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    /// <summary>Refuses new requests, waits for the request gate and returns a lease that reopens requests on release. Cancelling <paramref name="urgencyToken"/> cancels active requests.</summary>
    public async Task<IDisposable> EnterUnloadAsync(CancellationToken urgencyToken = default)
    {
        using CancellationTokenRegistration cancellation = FenceRequests(urgencyToken);
        await _gate.WaitAsync(urgent: false, CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>Records that no model is resident and disposes the source that cancels its requests.</summary>
    public void MarkUnloaded()
    {
        _requestAbort?.Dispose();
        Volatile.Write(ref _requestAbort, null);
        _model = null;
        _backend = null;
        _expertPlacement = null;
        Observe(Observation.State);
    }

    /// <summary>Releases the request cancellation source.</summary>
    public void Dispose()
    {
        _requestAbort?.Dispose();
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
        Volatile.Write(ref _observation, new(_model, _backend, state, DateTimeOffset.UtcNow, _expertPlacement));

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

    /// <summary>Holds the request gate for one request; disposing it releases the gate.</summary>
    public sealed class ExecutionLease : IAsyncDisposable
    {
        private readonly CancellationTokenSource _linked;
        private readonly bool _urgent;
        private RequestGate? _gate;

        internal ExecutionLease(RequestGate gate, CancellationTokenSource linked, bool urgent)
        {
            _gate = gate;
            _linked = linked;
            _urgent = urgent;
        }

        /// <summary>Cancelled when the caller cancels or an urgent unload cancels active requests.</summary>
        public CancellationToken CancellationToken => _linked.Token;

        /// <summary>
        /// Lets waiting urgent requests run before this request's next step, then takes the gate back before any other
        /// waiting request or lifecycle work. An engine calls it before each native run of a request made of several. It
        /// returns at once for an urgent request or when no urgent request waits.
        /// </summary>
        /// <gotcha>When the request is cancelled while it waits to take the gate back, it throws and the lease no longer holds the gate.</gotcha>
        public async ValueTask YieldAsync()
        {
            if (_urgent || _gate is not { } gate)
            {
                return;
            }

            try
            {
                await gate.YieldAsync(_linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _gate = null;
                throw;
            }
        }

        /// <summary>Disposes the linked token and releases the request gate once.</summary>
        public ValueTask DisposeAsync()
        {
            _linked.Dispose();
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Admits one holder at a time. A released gate goes to the first waiting urgent request, otherwise to the first other
    /// waiter. A request that yields to urgent requests waits ahead of every other waiter that is not urgent, so a load or an
    /// unload never runs between two runs of one request.
    /// </summary>
    internal sealed class RequestGate
    {
        private readonly Lock _lock = new();
        private readonly LinkedList<TaskCompletionSource> _urgent = new();
        private readonly LinkedList<TaskCompletionSource> _others = new();
        private bool _held;

        /// <summary>Completes when the caller holds the gate.</summary>
        internal Task WaitAsync(bool urgent, CancellationToken token)
        {
            LinkedListNode<TaskCompletionSource> node;
            lock (_lock)
            {
                token.ThrowIfCancellationRequested();
                if (!_held)
                {
                    _held = true;
                    return Task.CompletedTask;
                }

                node = (urgent ? _urgent : _others).AddLast(Waiter());
            }

            return WaitForAsync(node, token);
        }

        /// <summary>Hands the gate to the first urgent waiter, when there is one, and completes when the caller holds it again.</summary>
        internal Task YieldAsync(CancellationToken token)
        {
            LinkedListNode<TaskCompletionSource> node;
            lock (_lock)
            {
                if (_urgent.Count == 0)
                {
                    return Task.CompletedTask;
                }

                node = _others.AddFirst(Waiter());
                GrantLocked();
            }

            return WaitForAsync(node, token);
        }

        /// <summary>Passes the gate to the next waiter, or frees it.</summary>
        internal void Release()
        {
            lock (_lock)
            {
                if (!GrantLocked())
                {
                    _held = false;
                }
            }
        }

        private bool GrantLocked()
        {
            LinkedList<TaskCompletionSource> next = _urgent.Count != 0 ? _urgent : _others;
            if (next.First is not { } node)
            {
                return false;
            }

            next.RemoveFirst();
            node.Value.TrySetResult();
            return true;
        }

        // A waiter that is cancelled before the gate reaches it leaves its queue; one already granted holds the gate.
        private async Task WaitForAsync(LinkedListNode<TaskCompletionSource> node, CancellationToken token)
        {
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                lock (_lock)
                {
                    if (node.List is not { } queue)
                    {
                        return;
                    }

                    queue.Remove(node);
                }

                node.Value.TrySetCanceled(token);
            });
            await node.Value.Task.ConfigureAwait(false);
        }

        private static TaskCompletionSource Waiter() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
