using System.Collections.Concurrent;

namespace Darkspyre.Inference.Tensor.Common;

public sealed class TensorExecutionCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.Ordinal);
    private bool _disposed;

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        TensorModelKey key,
        TensorBackendKind backend,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);
        string executionKey = $"{key.ModelId}\n{key.Revision}\n{backend}";
        SemaphoreSlim gate = _gates.GetOrAdd(executionKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (SemaphoreSlim gate in _gates.Values)
        {
            gate.Dispose();
        }
    }

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
