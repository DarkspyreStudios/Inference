using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal sealed class LlmOperationLifetime : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Exception> _streamCleanupFailures = [];
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

    [SuppressMessage("Design", "CA1031", Justification = "Stream delivery preserves producer, reader and cancellation failures after actual producer cleanup.")]
    internal async IAsyncEnumerable<T> StreamAsync<T>(
        Func<CancellationToken, IAsyncEnumerable<T>> source,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Func<CancellationToken, Lease>? admit = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var events = Channel.CreateBounded<T>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        Lease operation = (admit ?? Enter)(stop.Token);
        CancellationToken ownerStop = _stop.Token;
        Task<(Exception? Failure, bool CleanupFailed)> producer = ProduceStreamAsync(source, events.Writer, operation);
        // Only the producer invokes source and admission callbacks after startup.
        source = null!;
        admit = null;
        Exception? readFailure = null;
        try
        {
            while (readFailure is null)
            {
                bool available;
                try
                {
                    available = await events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    ownerStop.ThrowIfCancellationRequested();
                    ThrowIfStopping();
                }
                catch (Exception error) { readFailure = error; break; }
                if (!available) break;
                while (events.Reader.TryRead(out T? item))
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ownerStop.ThrowIfCancellationRequested();
                        ThrowIfStopping();
                    }
                    catch (Exception error) { readFailure = error; break; }
                    yield return item;
                }
            }
        }
        finally
        {
            Exception? cancellationFailure = null;
            try { await stop.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { cancellationFailure = error; }
            var outcome = await producer.ConfigureAwait(false);
            // Abandonment drops unread operation events, not actual enumeration cleanup failures.
            Exception? producerFailure = readFailure is not null || outcome.CleanupFailed ? outcome.Failure : null;
            Exception? failure = CombineFailures(CombineFailures(readFailure, producerFailure), cancellationFailure);
            if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "The producer owns its lease through enumeration cleanup and preserves independent operation and cleanup failures.")]
    private async Task<(Exception? Failure, bool CleanupFailed)> ProduceStreamAsync<T>(
        Func<CancellationToken, IAsyncEnumerable<T>> source, ChannelWriter<T> events, Lease operation)
    {
        using (operation)
        {
            IAsyncEnumerator<T>? enumerator = null;
            Exception? failure = null;
            bool expectedCancellation = false;
            bool cleanupFailed = false;
            try
            {
                enumerator = source(operation.Token).GetAsyncEnumerator(operation.Token);
                source = null!;
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    operation.Token.ThrowIfCancellationRequested();
                    ThrowIfStopping();
                    await events.WriteAsync(enumerator.Current, operation.Token).ConfigureAwait(false);
                }
                operation.Token.ThrowIfCancellationRequested();
                ThrowIfStopping();
            }
            catch (Exception error)
            {
                failure = error;
                expectedCancellation = error is OperationCanceledException && operation.Token.IsCancellationRequested;
            }
            if (enumerator is not null)
            {
                try { await enumerator.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup)
                {
                    lock (_gate) { _streamCleanupFailures.Add(cleanup); }
                    failure = CombineFailures(failure, cleanup);
                    expectedCancellation = false;
                    cleanupFailed = true;
                }
            }
            source = null!;
            enumerator = null;
            events.TryComplete(failure);
            return (expectedCancellation ? null : failure, cleanupFailed);
        }
    }

    private static Exception? CombineFailures(Exception? first, Exception? second) =>
        first is null ? second : second is null || ReferenceEquals(first, second) ? first : new AggregateException(first, second);

    internal Task RetireAsync(Func<Task>? release = null)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_retired is not null) return _retired.Task;
            _retired = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_active == 0) _drained.TrySetResult();
        }
        _ = CompleteRetirementAsync(completion, release);
        return completion.Task;
    }

    public ValueTask DisposeAsync() => new(RetireAsync());

    public void Dispose() => RetireAsync().GetAwaiter().GetResult();

    [SuppressMessage("Design", "CA1031", Justification = "The retirement completion preserves failures for every disposal caller.")]
    private async Task CompleteRetirementAsync(TaskCompletionSource completion, Func<Task>? release)
    {
        try
        {
            Task cancellation = _stop.CancelAsync();
            Task retirement = DrainAndReleaseAsync(release);
            Task both = Task.WhenAll(cancellation, retirement);
            try { await both.ConfigureAwait(false); }
            catch
            {
                if (both.Exception is { InnerExceptions.Count: > 1 } failures) throw failures;
                throw;
            }
            _stop.Dispose();
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            _stop.Dispose();
            completion.TrySetException(error);
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "All owned release stages settle before their actual failures are reported.")]
    private async Task DrainAndReleaseAsync(Func<Task>? release)
    {
        await _drained.Task.ConfigureAwait(false);
        Exception? releaseFailure = null;
        try { if (release is not null) await release().ConfigureAwait(false); }
        catch (Exception error) { releaseFailure = error; }
        Exception[] streamFailures;
        lock (_gate) { streamFailures = [.. _streamCleanupFailures]; }
        if (streamFailures.Length == 0)
        {
            if (releaseFailure is not null) ExceptionDispatchInfo.Throw(releaseFailure);
            return;
        }
        Exception[] failures = new AggregateException(releaseFailure is null
            ? streamFailures : [.. streamFailures, releaseFailure]).Flatten().InnerExceptions
            .Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
        if (failures.Length == 1) ExceptionDispatchInfo.Throw(failures[0]);
        throw new AggregateException(failures);
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
