using System.Collections.Concurrent;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Serializes execution so only one operation at a time runs for each model revision and backend.</summary>
public sealed class TensorExecutionCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Waits for exclusive execution of the model on the backend. Disposing the returned handle releases it.</summary>
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

    /// <summary>Releases the execution gates. Further acquisitions throw <see cref="ObjectDisposedException"/>.</summary>
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
