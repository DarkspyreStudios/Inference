using System.Diagnostics.CodeAnalysis;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal sealed class LlmOperationLifetime : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _retired;
    private int _active;

    internal Lease Enter(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retired is not null, this);
            var lease = new Lease(this, _stop.Token, cancellationToken);
            _active++;
            return lease;
        }
    }

    internal void ThrowIfStopping()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_retired is not null, this); }
    }

    internal Task RetireAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_retired is not null) return _retired.Task;
            _retired = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_active == 0) _drained.TrySetResult();
        }
        _ = CompleteRetirementAsync(completion);
        return completion.Task;
    }

    public ValueTask DisposeAsync() => new(RetireAsync());

    public void Dispose() => RetireAsync().GetAwaiter().GetResult();

    [SuppressMessage("Design", "CA1031", Justification = "The retirement completion preserves failures for every disposal caller.")]
    private async Task CompleteRetirementAsync(TaskCompletionSource completion)
    {
        try
        {
            try { await _stop.CancelAsync().ConfigureAwait(false); }
            finally { await _drained.Task.ConfigureAwait(false); }
            _stop.Dispose();
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            _stop.Dispose();
            completion.TrySetException(error);
        }
    }

    private void Leave()
    {
        lock (_gate)
        {
            _active--;
            if (_retired is not null && _active == 0) _drained.TrySetResult();
        }
    }

    internal sealed class Lease : IDisposable
    {
        private LlmOperationLifetime? _owner;
        private readonly CancellationTokenSource? _linked;

        internal Lease(LlmOperationLifetime owner, CancellationToken stop, CancellationToken caller)
        {
            _owner = owner;
            if (caller.CanBeCanceled) _linked = CancellationTokenSource.CreateLinkedTokenSource(stop, caller);
            Token = _linked?.Token ?? stop;
        }

        internal CancellationToken Token { get; }

        public void Dispose()
        {
            LlmOperationLifetime? owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            _linked?.Dispose();
            owner.Leave();
        }
    }
}
