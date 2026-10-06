using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

public sealed partial class QwenLlmInference
{
    private readonly HashSet<StreamOperation> _streams = [];

    private StreamOperation StartStream(LlmInferenceRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_stopGate)
        {
            ThrowIfDisposed();
            ThrowIfReleaseRequired();
            var operation = new StreamOperation(this, token);
            _streams.Add(operation);
            operation.Start(request);
            return operation;
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "Reader cleanup preserves the actual producer and independent cancellation failures.")]
    private static async IAsyncEnumerable<LlmStreamEvent> ReadStreamAsync(QwenLlmInference owner,
        LlmInferenceRequest request, [EnumeratorCancellation] CancellationToken token)
    {
        StreamOperation operation = owner.StartStream(request, token);
        owner = null!;
        request = null!;
        Exception? failure = null;
        try
        {
            while (failure is null)
            {
                bool available;
                try
                {
                    available = await operation.Events.Reader.WaitToReadAsync(token).ConfigureAwait(false);
                    operation.FenceDelivery(token);
                }
                catch (Exception error) { failure = error; break; }
                if (!available) break;
                while (operation.Events.Reader.TryRead(out LlmStreamEvent? item))
                {
                    try { operation.FenceDelivery(token); }
                    catch (Exception error) { failure = error; break; }
                    yield return item;
                }
            }
        }
        finally
        {
            Exception? outcome = await operation.RetireReaderAsync().ConfigureAwait(false);
            if (failure is OperationCanceledException && token.IsCancellationRequested && outcome is not null)
            {
                failure = outcome; // Reader cancellation observes the producer's actual operation outcome.
                outcome = null;
            }
            // Abandonment drops unread operation results, not actual cleanup failures.
            if (failure is not null || operation.CleanupFailed)
            {
                Exception? combined = JoinStreamFailures(failure, outcome);
                if (combined is not null) ExceptionDispatchInfo.Throw(combined);
            }
        }
    }

    private async Task DrainStreamsAsync()
    {
        StreamOperation[] streams;
        lock (_stopGate) streams = [.. _streams];
        foreach (StreamOperation operation in streams) operation.StopDelivery();
        Exception?[] outcomes = await Task.WhenAll(streams.Select(operation => operation.Completion)).ConfigureAwait(false);
        Exception? failure = null;
        for (int index = 0; index < streams.Length; index++)
            if (streams[index].CleanupFailed) failure = JoinStreamFailures(failure, outcomes[index]);
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        lock (_stopGate)
        {
            foreach (StreamOperation operation in streams)
            {
                _streams.Remove(operation);
                operation.DetachOwner();
            }
        }
    }

    private void StreamSettled(StreamOperation operation)
    {
        lock (_stopGate)
        {
            if (operation.CleanupFailed) _releaseRequired = true;
        }
    }

    private static Exception? JoinStreamFailures(Exception? first, Exception? second)
    {
        if (first is null) return second;
        if (second is null || ReferenceEquals(first, second)) return first;
        IReadOnlyList<Exception> original = first is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [first];
        IReadOnlyList<Exception> additional = second is AggregateException other ? other.Flatten().InnerExceptions : [second];
        if (additional.All(error => original.Contains(error, ReferenceEqualityComparer.Instance))) return first;
        Exception[] causes = original.Concat(additional).Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray();
        return causes.Length == 1 ? causes[0] : new AggregateException(causes);
    }

    private sealed class StreamOperation
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _sourceStop;
        private readonly CancellationTokenSource _deliveryStop;
        private readonly TaskCompletionSource<Exception?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Exception> _cleanupFailures = [];
        // Failed release keeps the actual enumerator and callback owners reachable without replaying cleanup.
        private readonly List<object> _refusedOwners = [];
        private QwenLlmInference? _owner;
        private Task? _abort;
        private Task<Exception?>? _readerRetirement;
        private bool _settled;
        private bool _deliveryStopped;

        internal StreamOperation(QwenLlmInference owner, CancellationToken token)
        {
            _owner = owner;
            _sourceStop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _deliveryStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        }

        // Match managed Connect delivery's 32-event bound; native work never depends on a paused reader at teardown.
        internal Channel<LlmStreamEvent> Events { get; } = Channel.CreateBounded<LlmStreamEvent>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        internal Task<Exception?> Completion => _completion.Task;
        internal bool CleanupFailed { get { lock (_sync) return _cleanupFailures.Count != 0; } }

        internal void Start(LlmInferenceRequest request) =>
            _ = Task.Run(() => ProduceAsync(_owner!, request), CancellationToken.None);

        internal void DetachOwner() => Volatile.Write(ref _owner, null);

        internal void RecordCleanup(Exception error, object refusedOwner)
        {
            lock (_sync)
            {
                _cleanupFailures.Add(error);
                _refusedOwners.Add(refusedOwner);
            }
        }

        internal void FenceDelivery(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (_sync)
                if (_deliveryStopped) throw new OperationCanceledException("The Qwen stream delivery has retired.", token);
        }

        internal void StopDelivery()
        {
            lock (_sync)
            {
                _deliveryStopped = true;
                if (!_settled) _deliveryStop.Cancel();
            }
        }

        internal Task<Exception?> RetireReaderAsync()
        {
            lock (_sync) return _readerRetirement ??= Task.Run(RetireReaderCoreAsync, CancellationToken.None);
        }

        internal Task WaitForAbortAsync()
        {
            lock (_sync) return _abort ?? Task.CompletedTask;
        }

        private async Task<Exception?> RetireReaderCoreAsync()
        {
            StopDelivery();
            lock (_sync)
                if (!_settled) _abort ??= _sourceStop.CancelAsync();
            Exception? outcome = await Completion.ConfigureAwait(false);
            // A paused reader does not keep the engine rooted after completed teardown.
            QwenLlmInference? owner = Volatile.Read(ref _owner);
            if (owner is not null && !CleanupFailed)
            {
                lock (owner._stopGate)
                {
                    owner._streams.Remove(this);
                    DetachOwner();
                }
            }
            return outcome;
        }

        [SuppressMessage("Design", "CA1031", Justification = "The producer settles owned enumeration and cancellation before publishing all actual failures.")]
        private async Task ProduceAsync(QwenLlmInference owner, LlmInferenceRequest request)
        {
            IAsyncEnumerator<LlmStreamEvent>? source = null;
            Exception? failure = null;
            try
            {
                source = owner.StreamCoreAsync(request, this, _sourceStop.Token).GetAsyncEnumerator(_sourceStop.Token);
                request = null!;
                while (await source.MoveNextAsync().ConfigureAwait(false))
                {
                    try { await Events.Writer.WriteAsync(source.Current, _deliveryStop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_deliveryStop.IsCancellationRequested)
                    {
                        // Graceful model teardown drains actual work but no longer queues consumer events.
                    }
                }
            }
            catch (Exception error) { failure = error; }
            if (source is not null)
            {
                try { await source.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { RecordCleanup(cleanup, source); failure = JoinStreamFailures(failure, cleanup); }
            }
            Task? abort;
            lock (_sync) { _settled = true; abort = _abort; }
            if (abort is not null)
            {
                try { await abort.ConfigureAwait(false); }
                catch (Exception cleanup) { RecordCleanup(cleanup, _sourceStop); }
            }
            lock (_sync)
                foreach (Exception cleanup in _cleanupFailures) failure = JoinStreamFailures(failure, cleanup);
            _sourceStop.Dispose();
            _deliveryStop.Dispose();
            source = null;
            request = null!;
            owner.StreamSettled(this);
            owner = null!;
            Events.Writer.TryComplete(failure);
            _completion.TrySetResult(failure);
        }
    }
}
