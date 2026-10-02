using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Model lists kept for the registration owner's lifetime and shared by its <see cref="LlmProviderService"/> instances. A
/// connection's list stays until its exact version and resolved continuation-scope tuple changes or a refresh replaces it.
/// Concurrent requests for one connection share a single listing, and a failed listing is not kept.
/// <see cref="ServiceCollectionExtensions.AddLlmConnect(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/> registers one instance.
/// </summary>
public sealed class LlmModelListCache : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LlmOperationLifetime _lifetime = new();
    private bool _disposed;

    internal readonly record struct Scope(string Version, string? ResolvedContinuationScope);

    internal Task<IReadOnlyList<LlmModel>> GetAsync(string connectionId, Scope scope,
        Func<CancellationToken, Task<IReadOnlyList<LlmModel>>> list, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<LlmModel>> listing;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(connectionId, out Entry? entry) || entry.Scope != scope
                || entry.Listing.IsFaulted || entry.Listing.IsCanceled)
            {
                entry = Start(connectionId, scope, list);
            }
            listing = entry.Listing;
        }
        return listing.WaitAsync(cancellationToken);
    }

    internal Task<IReadOnlyList<LlmModel>> RefreshAsync(string connectionId, Scope scope,
        Func<CancellationToken, Task<IReadOnlyList<LlmModel>>> list, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<LlmModel>> listing;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            listing = Start(connectionId, scope, list).Listing;
        }
        return listing.WaitAsync(cancellationToken);
    }

    // Caller owns the gate. The listing is shared, so no single caller's cancellation stops it.
    private Entry Start(string connectionId, Scope scope, Func<CancellationToken, Task<IReadOnlyList<LlmModel>>> list)
    {
        LlmOperationLifetime.Lease operation = _lifetime.Enter(CancellationToken.None);
        var completion = new TaskCompletionSource<IReadOnlyList<LlmModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry(scope, completion.Task);
        _entries[connectionId] = entry;
        _ = Task.Run(() => CompleteListingAsync(connectionId, entry, completion, operation, list), CancellationToken.None);
        return entry;
    }

    [SuppressMessage("Design", "CA1031", Justification = "The shared listing preserves provider failures for its waiters and observes abandoned faults.")]
    private async Task CompleteListingAsync(
        string connectionId,
        Entry entry,
        TaskCompletionSource<IReadOnlyList<LlmModel>> completion,
        LlmOperationLifetime.Lease operation,
        Func<CancellationToken, Task<IReadOnlyList<LlmModel>>> list)
    {
        using (operation)
        {
            try
            {
                operation.Token.ThrowIfCancellationRequested();
                IReadOnlyList<LlmModel> models = await list(operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
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
                lock (_gate)
                {
                    if (_disposed || !_entries.TryGetValue(connectionId, out Entry? current) || !ReferenceEquals(current, entry))
                        completion.TrySetCanceled(operation.Token);
                    else completion.TrySetResult(models);
                }
            }
            catch (OperationCanceledException error)
            {
                completion.TrySetCanceled(error.CancellationToken);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
                _ = completion.Task.Exception;
            }
        }
    }

    /// <summary>Fences listings, clears kept models, and cancels and awaits every owned listing, including replaced entries.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _entries.Clear();
        }
        return new(_lifetime.RetireAsync());
    }

    /// <summary>Synchronously waits for cache retirement. Use asynchronous disposal while provider work is active.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed record Entry(Scope Scope, Task<IReadOnlyList<LlmModel>> Listing);
}
