using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Owns immutable model facts and shared producers for a registration. Exact version/scope changes replace every
/// family view. Caller cancellation stops only that caller's wait.
/// </summary>
public sealed class LlmModelListCache : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Epoch> _entries = new(StringComparer.Ordinal);
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly LlmCleanupOutcomes _cleanupOutcomes = new();
    private bool _disposed;

    internal readonly record struct Scope(string Version, string? ResolvedContinuationScope);

    internal Task<IReadOnlyList<LlmModel>> GetAsync(string connectionId, Scope scope, ILlmClient client,
        LlmConnection snapshot, ModelCatalogFamily family, CancellationToken cancellationToken) =>
        AcquireAsync(connectionId, scope, client, snapshot, family, false, cancellationToken);

    internal Task<IReadOnlyList<LlmModel>> RefreshAsync(string connectionId, Scope scope, ILlmClient client,
        LlmConnection snapshot, ModelCatalogFamily family, CancellationToken cancellationToken) =>
        AcquireAsync(connectionId, scope, client, snapshot, family, true, cancellationToken);

    private async Task<IReadOnlyList<LlmModel>> AcquireAsync(string connectionId, Scope scope, ILlmClient client,
        LlmConnection snapshot, ModelCatalogFamily family, bool refresh, CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease admission = _lifetime.Enter(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.Version);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(family)) throw new ArgumentOutOfRangeException(nameof(family));
        if (snapshot.Version != scope.Version) throw new ArgumentException("The catalog scope must match the admitted connection.", nameof(scope));
        ThrowIfCanceled(admission.Token);
        Task<IReadOnlyList<LlmModel>> listing;
        lock (_gate)
        {
            ThrowIfCanceled(admission.Token);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (refresh || !_entries.TryGetValue(connectionId, out Epoch? epoch) || epoch.Scope != scope)
            {
                epoch = new(scope);
                _entries[connectionId] = epoch;
            }
            if (!epoch.Families.TryGetValue(family, out Task<IReadOnlyList<LlmModel>>? current)
                || current.IsCanceled || current.IsFaulted)
            {
                var completion = new TaskCompletionSource<IReadOnlyList<LlmModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
                current = completion.Task;
                epoch.Families[family] = current;
                var producer = new Producer(client, snapshot);
                LlmOperationLifetime.Lease operation = _lifetime.Enter(CancellationToken.None);
                _ = Task.Run(() => ProduceFamilyAsync(connectionId, epoch, producer, operation, completion), CancellationToken.None);
            }
            listing = current;
        }
        // A facade's retirement/cancellation cannot retire the cache producer.
        IReadOnlyList<LlmModel> published = await listing.WaitAsync(admission.Token).ConfigureAwait(false);
        ThrowIfCanceled(admission.Token);
        return published;
    }

    [SuppressMessage("Design", "CA1031", Justification = "The shared completion preserves provider failures and observes abandoned faults.")]
    private async Task ProduceFamilyAsync(string connectionId, Epoch epoch, Producer producer,
        LlmOperationLifetime.Lease operation, TaskCompletionSource<IReadOnlyList<LlmModel>> completion)
    {
        using (operation)
        {
            try
            {
                void Fence() => CheckEpoch(connectionId, epoch, operation.Token);
                Fence();
                IReadOnlyList<LlmModel> models = await producer.Client!.ListModelsAsync(producer.Snapshot!, operation.Token).ConfigureAwait(false);
                Fence();
                models = Array.AsReadOnly(models.Select(model => model with
                {
                    CatalogEntry = model.CatalogEntry?.Clone(),
                    Capabilities = model.Capabilities is not { } capabilities ? null : capabilities with
                    {
                        ReasoningLevels = capabilities.ReasoningLevels.ToFrozenSet(),
                        PromptCache = capabilities.PromptCache with { Retentions = capabilities.PromptCache.Retentions.ToFrozenSet() },
                        Tools = capabilities.Tools with { ChoiceModes = capabilities.Tools.ChoiceModes.ToFrozenSet() },
                        Agent = capabilities.Agent with { SupportedRuntimePolicies = capabilities.Agent.SupportedRuntimePolicies.ToFrozenSet() }
                    }
                }).ToArray());
                Fence();
                producer.Clear();
                lock (_gate)
                {
                    CheckEpochLocked(connectionId, epoch, operation.Token);
                    completion.TrySetResult(models);
                }
            }
            catch (Exception error) { CompleteFailure(completion, error); }
            finally { producer.Clear(); }
        }
    }

    private void CompleteFailure<T>(TaskCompletionSource<T> completion, Exception error)
    {
        _cleanupOutcomes.Record(error);
        if (error is OperationCanceledException canceled && !LlmResourcePreparation.IsCleanupFailure(error)) completion.TrySetCanceled(canceled.CancellationToken);
        else { completion.TrySetException(error); _ = completion.Task.Exception; }
    }

    private void ThrowIfCanceled(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _lifetime.ThrowIfStopping(); }
        catch (ObjectDisposedException error) { throw new OperationCanceledException("Catalog production was canceled by retirement.", error, token); }
    }

    private void CheckEpoch(string connectionId, Epoch epoch, CancellationToken token)
    {
        ThrowIfCanceled(token);
        lock (_gate) CheckEpochLocked(connectionId, epoch, token);
    }

    private void CheckEpochLocked(string connectionId, Epoch epoch, CancellationToken token)
    {
        ThrowIfCanceled(token);
        if (_disposed || !_entries.TryGetValue(connectionId, out Epoch? current) || !ReferenceEquals(epoch, current))
            throw new OperationCanceledException("The catalog epoch was replaced.", token);
    }

    /// <summary>Fences, cancels and drains every producer, clears facts, and preserves refused owned cleanup.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate) { _disposed = true; _entries.Clear(); }
        return new(_lifetime.RetireAsync(_cleanupOutcomes.ReleaseAsync));
    }

    /// <summary>Synchronously retires this cache. Use asynchronous disposal while listings are active.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Epoch(Scope scope)
    {
        internal Scope Scope { get; } = scope;
        internal Dictionary<ModelCatalogFamily, Task<IReadOnlyList<LlmModel>>> Families { get; } = [];
    }

    private sealed class Producer(ILlmClient client, LlmConnection snapshot)
    {
        internal ILlmClient? Client { get; private set; } = client;
        internal LlmConnection? Snapshot { get; private set; } = snapshot;
        internal void Clear() { Client = null; Snapshot = null; }
    }
}

internal enum ModelCatalogFamily { Chat }
