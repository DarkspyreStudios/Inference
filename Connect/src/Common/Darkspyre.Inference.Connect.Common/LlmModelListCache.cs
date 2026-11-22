using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Owns immutable model facts and serialized fresh productions for committed connections and isolated settings views.
/// Caller cancellation stops that caller's wait, not a dispatched producer.
/// </summary>
public sealed class LlmModelListCache : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string View, string Connection), Entry> _entries = [];
    private readonly List<Task> _retirements = [];
    private readonly LlmOperationLifetime _lifetime = new();
    private readonly LlmCleanupOutcomes _cleanupOutcomes = new();
    private bool _disposed;

    /// <summary>Reports completion or invalidation of one committed connection's owned model facts.</summary>
    /// <gotcha>Callbacks run on the publishing thread. Read the publication and dispatch rendering;
    /// do not start another refresh in response to each publication.</gotcha>
    public event Action<string>? Changed;

    /// <summary>Reads already-published committed facts at the exact connection version, without provider work.</summary>
    public LlmPublishedModelList? ReadPublished(string connectionId, string version)
    {
        lock (_gate)
            return _entries.TryGetValue(("committed", connectionId), out Entry? entry)
                && entry.Current.Scope.Version == version
                    ? entry.Current.Published ?? (entry.Current.Families.Count > 0 ? new(version, false, null, null) : null)
                    : null;
    }

    /// <summary>Admits one initial committed read for an unlisted version, including its source-resolution window.</summary>
    /// <gotcha>The callback runs on the owner's lifetime and thread pool. It must call the same canonical fresh read.
    /// Natural UI fresh demands do not use this one-time admission and are never coalesced here.</gotcha>
    public void EnsureInitialRead(string connectionId, string version, Func<CancellationToken, Task> refresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(refresh);
        Entry entry;
        Epoch epoch;
        LlmOperationLifetime.Lease operation;
        var key = (View: "committed", Connection: connectionId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out Entry? current) && current.Current.Scope.Version == version
                && (current.Current.Published is not null || current.Current.Families.Count > 0)) return;
            operation = _lifetime.Enter(CancellationToken.None);
            // Empty definition scope denotes admission only. The real producer replaces it with its admitted scope.
            var scope = new ModelListScope(version, null, "committed", string.Empty);
            if (current is null) _entries.Add(key, entry = new(scope));
            else
            {
                entry = current;
                if (entry.Current.Scope.Version != version) ReplaceEpoch(entry, scope);
            }
            epoch = entry.Current;
            epoch.Published = new(version, false, null, null);
        }
        _ = Task.Run(async () =>
        {
            using (operation)
            {
                try { await refresh(operation.Token).ConfigureAwait(false); }
                catch (Exception error) { _cleanupOutcomes.Record(error); }
                bool notify = false;
                lock (_gate)
                {
                    if (!_disposed && _entries.TryGetValue(key, out Entry? current) && ReferenceEquals(entry, current)
                        && ReferenceEquals(epoch, current.Current) && epoch.Published is { Listed: false }
                        && epoch.Families.Count == 0)
                    {
                        // Resolution can fail before a producer exists. Complete the attempt without a render retry.
                        epoch.Published = new(version, true, null, "Couldn't list models.");
                        notify = true;
                    }
                }
                if (notify) Changed?.Invoke(connectionId);
            }
        });
    }

    /// <summary>Invalidates committed facts after a real model operation, without calling the provider.</summary>
    public void Invalidate(string connectionId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(("committed", connectionId), out Entry? entry))
                ReplaceEpoch(entry, entry.Current.Scope);
        }
        Changed?.Invoke(connectionId);
    }

    internal void RetireView(string viewId)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _retirements.RemoveAll(task => task.IsCompletedSuccessfully);
            foreach (var key in _entries.Keys.Where(key => key.View == viewId).ToArray())
            {
                Entry entry = _entries[key];
                _entries.Remove(key);
                _retirements.Add(entry.Lifetime.RetireAsync(() =>
                {
                    entry.Dispatch.Dispose();
                    return Task.CompletedTask;
                }));
            }
        }
    }

    internal Task<LlmModelListing> GetAsync(string connectionId, ModelListScope scope, ILlmClient client,
        LlmConnection snapshot, ModelCatalogFamily family, CancellationToken cancellationToken) =>
        AcquireAsync(connectionId, scope, client, snapshot, family, false, cancellationToken);

    internal Task<LlmModelListing> RefreshAsync(string connectionId, ModelListScope scope, ILlmClient client,
        LlmConnection snapshot, ModelCatalogFamily family, CancellationToken cancellationToken) =>
        AcquireAsync(connectionId, scope, client, snapshot, family, true, cancellationToken);

    private async Task<LlmModelListing> AcquireAsync(string connectionId, ModelListScope scope, ILlmClient client,
        LlmConnection snapshot, ModelCatalogFamily family, bool refresh, CancellationToken cancellationToken)
    {
        using LlmOperationLifetime.Lease admission = _lifetime.Enter(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.Version);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(family)) throw new ArgumentOutOfRangeException(nameof(family));
        if (snapshot.Version != scope.Version) throw new ArgumentException("The catalog scope must match the admitted connection.", nameof(scope));
        Entry entry;
        Epoch epoch;
        Task<LlmModelListing> listing;
        LlmOperationLifetime.Lease viewAdmission;
        lock (_gate)
        {
            ThrowIfCanceled(admission.Token);
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = (scope.ViewId, connectionId);
            if (!_entries.TryGetValue(key, out entry!)) _entries.Add(key, entry = new(scope));
            else if (!refresh && entry.Current.Scope != scope) ReplaceEpoch(entry, scope);
            epoch = entry.Current;
            viewAdmission = entry.Lifetime.Enter(admission.Token);
            if (refresh || !epoch.Families.TryGetValue(family, out Task<LlmModelListing>? current)
                || current.IsCanceled || current.IsFaulted)
            {
                var completion = new TaskCompletionSource<LlmModelListing>(TaskCreationOptions.RunContinuationsAsynchronously);
                listing = completion.Task;
                // A fresh demand never replaces or joins an older production before obtaining its dispatch slot.
                if (!refresh) epoch.Families[family] = listing;
                var producer = new Producer(client, snapshot);
                LlmOperationLifetime.Lease viewOperation = entry.Lifetime.Enter(CancellationToken.None);
                LlmOperationLifetime.Lease operation = _lifetime.Enter(viewOperation.Token);
                _ = Task.Run(() => ProduceAsync(key, entry, epoch, scope, family, producer, operation, viewOperation,
                    completion, refresh, viewAdmission.Token), CancellationToken.None);
            }
            else listing = current;
        }
        using (viewAdmission)
        {
            while (true)
            {
                try
                {
                    LlmModelListing published = await listing.WaitAsync(viewAdmission.Token).ConfigureAwait(false);
                    ThrowIfCanceled(viewAdmission.Token);
                    return published;
                }
                catch (OperationCanceledException) when (!refresh && !viewAdmission.Token.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        if (_disposed || epoch.Next is null) throw;
                        while (epoch.Next is { } next) epoch = next;
                        if (!epoch.Families.TryGetValue(family, out listing!)) throw;
                    }
                }
            }
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "The shared completion preserves provider failures and observes abandoned faults.")]
    private async Task ProduceAsync((string View, string Connection) key, Entry entry, Epoch epoch,
        ModelListScope scope, ModelCatalogFamily family, Producer producer, LlmOperationLifetime.Lease operation,
        LlmOperationLifetime.Lease viewOperation, TaskCompletionSource<LlmModelListing> completion,
        bool refresh, CancellationToken dispatchCancellation)
    {
        using (operation)
        using (viewOperation)
        {
            bool dispatched = false;
            bool notify = false;
            try
            {
                // Before fresh dispatch only that demand can cancel its queue position. Once dispatched the owner
                // retains both the slot and producer until actual completion, even when the caller stops waiting.
                await entry.Dispatch.WaitAsync(refresh ? dispatchCancellation : operation.Token).ConfigureAwait(false);
                dispatched = true;
                if (refresh) dispatchCancellation.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    ThrowIfCanceled(operation.Token);
                    if (refresh)
                    {
                        if (_disposed || !_entries.TryGetValue(key, out Entry? current) || !ReferenceEquals(entry, current)
                            || !ReferenceEquals(epoch, current.Current) && current.Current.Scope != scope)
                            throw new OperationCanceledException("The catalog epoch was replaced.", operation.Token);
                        // A canceled queued demand changes no facts or producer. A fresh scope becomes current
                        // only at dispatch, after the preceding actual production has finished.
                        if (current.Current.Scope != scope) ReplaceEpoch(current, scope);
                        epoch = current.Current;
                    }
                    CheckEpochLocked(key, entry, epoch, operation.Token);
                    if (!refresh && epoch.Families.TryGetValue(family, out Task<LlmModelListing>? newer)
                        && !ReferenceEquals(newer, completion.Task) && newer.IsCompletedSuccessfully)
                    {
                        completion.TrySetResult(newer.Result);
                        return;
                    }
                    epoch.Families[family] = completion.Task;
                }
                LlmModelListing listing = await producer.Client!.ListModelsAsync(producer.Snapshot!, operation.Token).ConfigureAwait(false);
                CheckEpoch(key, entry, epoch, operation.Token);
                var (complete, diagnostic) = CompleteCapabilities(producer.Client!, producer.Snapshot!, listing, operation.Token);
                listing = Freeze(complete);
                lock (_gate)
                {
                    CheckEpochLocked(key, entry, epoch, operation.Token);
                    epoch.Published = new(epoch.Scope.Version, true, listing, diagnostic);
                    completion.TrySetResult(listing);
                }
                notify = true;
            }
            catch (Exception error)
            {
                bool published = false;
                lock (_gate)
                {
                    if (dispatched && !operation.Token.IsCancellationRequested && !_disposed
                        && _entries.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry)
                        && ReferenceEquals(current.Current, epoch)
                        && ReferenceEquals(epoch.Families.GetValueOrDefault(family), completion.Task))
                    {
                        LlmConnection snapshot = producer.Snapshot!;
                        string diagnostic = Diagnostic(error.Message, snapshot);
                        epoch.Published = new(epoch.Scope.Version, true, null, diagnostic);
                        published = true;
                    }
                }
                CompleteFailure(completion, error);
                notify = published;
            }
            finally
            {
                producer.Clear();
                if (dispatched) entry.Dispatch.Release();
            }
            if (notify && key.View == "committed") Changed?.Invoke(key.Connection);
        }
    }

    private static (LlmModelListing Listing, string? Diagnostic) CompleteCapabilities(ILlmClient client,
        LlmConnection snapshot, LlmModelListing listing, CancellationToken token)
    {
        LlmConnection catalog = snapshot with { Catalog = listing.Models };
        string? problem = null;
        LlmModel[] models = listing.Models.Select(model =>
        {
            token.ThrowIfCancellationRequested();
            if (model.Capabilities is not null) return model;
            try { return model with { Capabilities = client.GetCapabilities(catalog, model.Id) }; }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                // Capability metadata is independent per model. A bad row does not discard the other fetched facts.
                problem ??= Diagnostic($"Capabilities unavailable for '{model.Id}': {error.Message}", snapshot);
                return model;
            }
        }).ToArray();
        return (listing with { Models = models }, problem);
    }

    private static string Diagnostic(string message, LlmConnection snapshot) =>
        SecretRedactor.Sanitize(message, LlmCredential.Parse(snapshot.Credential),
            snapshot.AdditionalHeaders.Values.Concat(snapshot.ModelOwner is { } owner ? [owner.Proof] : []));

    private static LlmModelListing Freeze(LlmModelListing listing) => listing with
    {
        Models = Array.AsReadOnly(listing.Models.Select(model => model with
        {
            CatalogEntry = model.CatalogEntry?.Clone(),
            Capabilities = model.Capabilities is not { } capabilities ? null : capabilities with
            {
                ReasoningLevels = capabilities.ReasoningLevels.ToFrozenSet(),
                PromptCache = capabilities.PromptCache with { Retentions = capabilities.PromptCache.Retentions.ToFrozenSet() },
                Tools = capabilities.Tools with { ChoiceModes = capabilities.Tools.ChoiceModes.ToFrozenSet() },
                Agent = capabilities.Agent with { SupportedRuntimePolicies = capabilities.Agent.SupportedRuntimePolicies.ToFrozenSet() }
            }
        }).ToArray()),
        ReservationModels = Array.AsReadOnly(listing.ReservationModels.Select(model => model with
        {
            Holders = Array.AsReadOnly(model.Holders.ToArray())
        }).ToArray())
    };

    private static void ReplaceEpoch(Entry entry, ModelListScope scope)
    {
        var next = new Epoch(scope);
        entry.Current.Next = next;
        entry.Current = next;
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

    private void CheckEpoch((string View, string Connection) key, Entry entry, Epoch epoch, CancellationToken token)
    {
        ThrowIfCanceled(token);
        lock (_gate) CheckEpochLocked(key, entry, epoch, token);
    }

    private void CheckEpochLocked((string View, string Connection) key, Entry entry, Epoch epoch, CancellationToken token)
    {
        ThrowIfCanceled(token);
        if (_disposed || !_entries.TryGetValue(key, out Entry? current) || !ReferenceEquals(entry, current)
            || !ReferenceEquals(epoch, current.Current))
            throw new OperationCanceledException("The catalog epoch was replaced.", token);
    }

    /// <summary>Fences, cancels and drains every producer, clears facts, and preserves refused owned cleanup.</summary>
    public ValueTask DisposeAsync()
    {
        Entry[] entries;
        Task[] retirements;
        lock (_gate)
        {
            _disposed = true;
            entries = _entries.Values.ToArray();
            retirements = _retirements.ToArray();
            _entries.Clear();
            _retirements.Clear();
            Changed = null;
        }
        return new(_lifetime.RetireAsync(async () =>
        {
            await Task.WhenAll(retirements.Concat(entries.Select(entry => entry.Lifetime.RetireAsync(() =>
            {
                entry.Dispatch.Dispose();
                return Task.CompletedTask;
            })))).ConfigureAwait(false);
            await _cleanupOutcomes.ReleaseAsync().ConfigureAwait(false);
        }));
    }

    /// <summary>Synchronously retires this cache. Use asynchronous disposal while listings are active.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Entry(ModelListScope scope)
    {
        internal Epoch Current { get; set; } = new(scope);
        internal SemaphoreSlim Dispatch { get; } = new(1, 1);
        internal LlmOperationLifetime Lifetime { get; } = new();
    }

    private sealed class Epoch(ModelListScope scope)
    {
        internal ModelListScope Scope { get; } = scope;
        internal Epoch? Next { get; set; }
        internal LlmPublishedModelList? Published { get; set; }
        internal Dictionary<ModelCatalogFamily, Task<LlmModelListing>> Families { get; } = [];
    }

    private sealed class Producer(ILlmClient client, LlmConnection snapshot)
    {
        internal ILlmClient? Client { get; private set; } = client;
        internal LlmConnection? Snapshot { get; private set; } = snapshot;
        internal void Clear() { Client = null; Snapshot = null; }
    }
}

internal readonly record struct ModelListScope(string Version, string? ResolvedContinuationScope,
    string ViewId, string DefinitionScope);

internal enum ModelCatalogFamily { Chat }
