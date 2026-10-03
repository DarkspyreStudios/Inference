using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Owns immutable model facts and shared producers for a registration. Exact version/scope and admitted xAI
/// configuration changes replace every family view. Caller cancellation stops only that caller's wait.
/// </summary>
public sealed class LlmModelListCache : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Epoch> _entries = new(StringComparer.Ordinal);
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly List<Exception> _cleanupFailures = [];
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
        var catalogClient = client as ILlmModelCatalogClient;
        if (catalogClient is null && family != ModelCatalogFamily.Chat)
            throw new NotSupportedException("This adapter does not implement a family catalog.");

        // Caller dictionaries/getters and adapter validation execute outside the cache gate.
        LlmConnection frozen = catalogClient is null ? snapshot : CatalogConnectionData.FreezeXai(snapshot);
        CatalogConnectionData.Evidence? evidence = catalogClient is null ? null : new(frozen);
        bool listingDisabled = catalogClient is not null && family == ModelCatalogFamily.Chat
            && !client.GetCapabilities(frozen).SupportsModelListing;
        ThrowIfCanceled(admission.Token);
        Task<IReadOnlyList<LlmModel>> listing;
        lock (_gate)
        {
            ThrowIfCanceled(admission.Token);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (refresh || !_entries.TryGetValue(connectionId, out Epoch? epoch) || epoch.Scope != scope
                || !SameEvidence(epoch.Evidence, evidence))
            {
                epoch = new(scope, evidence);
                _entries[connectionId] = epoch;
            }
            if (!epoch.Families.TryGetValue(family, out Task<IReadOnlyList<LlmModel>>? current)
                || current.IsCanceled || current.IsFaulted)
            {
                if (listingDisabled)
                {
                    current = Task.FromResult<IReadOnlyList<LlmModel>>(Array.AsReadOnly(Array.Empty<LlmModel>()));
                    epoch.Families[family] = current;
                }
                else
                {
                    Task<IReadOnlyList<JsonElement>>? generic = null;
                    if (catalogClient is not null)
                    {
                        if (epoch.Generic is null || epoch.Generic.IsCanceled || epoch.Generic.IsFaulted)
                        {
                            var genericCompletion = new TaskCompletionSource<IReadOnlyList<JsonElement>>(TaskCreationOptions.RunContinuationsAsynchronously);
                            epoch.Generic = genericCompletion.Task;
                            var job = new Producer(client, frozen);
                            LlmOperationLifetime.Lease lease = _lifetime.Enter(CancellationToken.None);
                            _ = Task.Run(() => ProduceGenericAsync(connectionId, epoch, job, lease, genericCompletion), CancellationToken.None);
                        }
                        generic = epoch.Generic;
                    }
                    var completion = new TaskCompletionSource<IReadOnlyList<LlmModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    current = completion.Task;
                    epoch.Families[family] = current;
                    var producer = new Producer(client, frozen);
                    LlmOperationLifetime.Lease operation = _lifetime.Enter(CancellationToken.None);
                    _ = Task.Run(() => ProduceFamilyAsync(connectionId, epoch, family, producer, generic, operation, completion), CancellationToken.None);
                }
            }
            listing = current;
        }
        // A facade's retirement/cancellation cannot retire the cache producer.
        IReadOnlyList<LlmModel> published = await listing.WaitAsync(admission.Token).ConfigureAwait(false);
        ThrowIfCanceled(admission.Token);
        return published;
    }

    [SuppressMessage("Design", "CA1031", Justification = "The shared completion preserves provider failures and observes abandoned faults.")]
    private async Task ProduceGenericAsync(string connectionId, Epoch epoch, Producer producer,
        LlmOperationLifetime.Lease operation, TaskCompletionSource<IReadOnlyList<JsonElement>> completion)
    {
        using (operation)
        {
            try
            {
                void Fence() => CheckEpoch(connectionId, epoch, operation.Token);
                Fence();
                IReadOnlyList<JsonElement> rows = await ((ILlmModelCatalogClient)producer.Client!).ReadCatalogPartAsync(
                    producer.Snapshot!, ModelCatalogPart.Generic, Fence, operation.Token).ConfigureAwait(false);
                Fence();
                rows = Array.AsReadOnly(rows.Select(row => row.Clone()).ToArray());
                Fence();
                producer.Clear();
                lock (_gate)
                {
                    CheckEpochLocked(connectionId, epoch, operation.Token);
                    completion.TrySetResult(rows);
                }
            }
            catch (Exception error) { CompleteFailure(completion, error); }
            finally { producer.Clear(); }
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "The shared completion preserves provider failures and observes abandoned faults.")]
    private async Task ProduceFamilyAsync(string connectionId, Epoch epoch, ModelCatalogFamily family, Producer producer,
        Task<IReadOnlyList<JsonElement>>? generic, LlmOperationLifetime.Lease operation,
        TaskCompletionSource<IReadOnlyList<LlmModel>> completion)
    {
        using (operation)
        {
            try
            {
                void Fence() => CheckEpoch(connectionId, epoch, operation.Token);
                Fence();
                IReadOnlyList<LlmModel> models;
                if (generic is null)
                    models = await producer.Client!.ListModelsAsync(producer.Snapshot!, operation.Token).ConfigureAwait(false);
                else
                {
                    IReadOnlyList<JsonElement> shared = await generic.ConfigureAwait(false);
                    Fence();
                    var catalog = (ILlmModelCatalogClient)producer.Client!;
                    ModelCatalogPart part = family switch
                    {
                        ModelCatalogFamily.Chat => ModelCatalogPart.Chat,
                        ModelCatalogFamily.Image => ModelCatalogPart.Image,
                        ModelCatalogFamily.Video => ModelCatalogPart.Video,
                        ModelCatalogFamily.Embedding => ModelCatalogPart.Embedding,
                        _ => throw new ArgumentOutOfRangeException(nameof(family))
                    };
                    IReadOnlyList<JsonElement> specific = await catalog.ReadCatalogPartAsync(producer.Snapshot!, part, Fence, operation.Token).ConfigureAwait(false);
                    Fence();
                    models = catalog.ProjectCatalog(producer.Snapshot!, family, shared, specific);
                }
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
        RecordCleanupFailure(error);
        if (error is OperationCanceledException canceled && !LlmResourcePreparation.IsCleanupFailure(error)) completion.TrySetCanceled(canceled.CancellationToken);
        else { completion.TrySetException(error); _ = completion.Task.Exception; }
    }

    private void RecordCleanupFailure(Exception error)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var failures = new List<Exception>();
        pending.Push(error);
        while (pending.TryPop(out Exception? current))
        {
            if (!visited.Add(current)) continue;
            if (current is InferenceResourceOperationException resource && resource.Failure.Phase == InferenceResourceOperationPhase.Cleanup
                || current is InferenceResourceOperationCanceledException canceled && canceled.Failure.Phase == InferenceResourceOperationPhase.Cleanup)
                failures.Add(current);
            if (current is AggregateException aggregate)
                for (int index = aggregate.InnerExceptions.Count - 1; index >= 0; index--) pending.Push(aggregate.InnerExceptions[index]);
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        lock (_gate)
            foreach (Exception failure in failures)
                if (!_cleanupFailures.Any(existing => ReferenceEquals(existing, failure))) _cleanupFailures.Add(failure);
    }

    private static bool SameEvidence(CatalogConnectionData.Evidence? left, CatalogConnectionData.Evidence? right) =>
        left is null || right is null ? left is null && right is null : left.Same(right);

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
        return new(_lifetime.RetireAsync(ReleaseAsync));
    }

    private Task ReleaseAsync()
    {
        Exception[] failures;
        lock (_gate) failures = _cleanupFailures.ToArray();
        if (failures.Length == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Length > 1) throw new AggregateException(failures);
        return Task.CompletedTask;
    }

    /// <summary>Synchronously retires this cache. Use asynchronous disposal while listings are active.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Epoch(Scope scope, CatalogConnectionData.Evidence? evidence)
    {
        internal Scope Scope { get; } = scope;
        internal CatalogConnectionData.Evidence? Evidence { get; } = evidence;
        internal Task<IReadOnlyList<JsonElement>>? Generic { get; set; }
        internal Dictionary<ModelCatalogFamily, Task<IReadOnlyList<LlmModel>>> Families { get; } = [];
    }

    private sealed class Producer(ILlmClient client, LlmConnection snapshot)
    {
        internal ILlmClient? Client { get; private set; } = client;
        internal LlmConnection? Snapshot { get; private set; } = snapshot;
        internal void Clear() { Client = null; Snapshot = null; }
    }
}
