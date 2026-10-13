using System.Runtime.CompilerServices;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Resource members for a runtime that runs no background work of its own.</summary>
internal sealed class NoAgentResources(LlmAgentResourceStopStatus stop) : IDisposable
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _observing;

    public async IAsyncEnumerable<LlmAgentEvent> ObserveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _observing, 1) == 1)
        {
            throw new InvalidOperationException("The session feed has one reader at a time.");
        }
        try
        {
            await _disposed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _observing, 0);
        }
        yield break;
    }

    public static Task<IReadOnlyList<LlmAgentResource>> GetAsync() => Task.FromResult<IReadOnlyList<LlmAgentResource>>([]);

    public static Task<LlmAgentResourceOutput> ReadAsync() =>
        Task.FromResult(new LlmAgentResourceOutput(LlmAgentResourceOutputStatus.Unavailable, ReadOnlyMemory<byte>.Empty, null, null, true));

    public Task<LlmAgentResourceStopStatus> StopAsync() => Task.FromResult(stop);

    public void Dispose() => _disposed.TrySetResult();
}
