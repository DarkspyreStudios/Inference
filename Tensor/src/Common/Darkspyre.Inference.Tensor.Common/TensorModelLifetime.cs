using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Coordinates one resident model with requests and host-directed teardown.</summary>
public sealed class TensorModelLifetime : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _requestAbort;
    private int _unloading;

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

    public void MarkLoaded()
    {
        if (_requestAbort is not null)
        {
            throw new InvalidOperationException("A model is already loaded.");
        }

        Volatile.Write(ref _requestAbort, new CancellationTokenSource());
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
        Volatile.Write(ref _unloading, 1);
        CancellationTokenSource? requestAbort = Volatile.Read(ref _requestAbort);
        using CancellationTokenRegistration cancellation = requestAbort is null
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
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        return new GateLease(this, reopenRequests: true);
    }

    public void MarkUnloaded()
    {
        _requestAbort?.Dispose();
        Volatile.Write(ref _requestAbort, null);
    }

    public void Dispose()
    {
        _requestAbort?.Dispose();
        _gate.Dispose();
    }

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
