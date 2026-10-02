using System.Diagnostics.CodeAnalysis;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Model lists kept for the registration owner's lifetime and shared by its <see cref="LlmProviderService"/> instances. A
/// connection's list stays until its <see cref="LlmConnection.Version"/> changes or a refresh replaces it.
/// Concurrent requests for one connection share a single listing, and a failed listing is not kept.
/// <see cref="ServiceCollectionExtensions.AddLlmConnect(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/> registers one instance.
/// </summary>
public sealed class LlmModelListCache : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LlmOperationLifetime _lifetime = new();
    private bool _disposed;

    internal Task<IReadOnlyList<LlmModel>> GetAsync(string connectionId, string version,
        Func<CancellationToken, Task<IReadOnlyList<LlmModel>>> list, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<LlmModel>> listing;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(connectionId, out Entry? entry) || entry.Version != version
                || entry.Listing.IsFaulted || entry.Listing.IsCanceled)
            {
                entry = Start(connectionId, version, list);
            }
            listing = entry.Listing;
        }
        return listing.WaitAsync(cancellationToken);
    }

    internal Task<IReadOnlyList<LlmModel>> RefreshAsync(string connectionId, string version,
        Func<CancellationToken, Task<IReadOnlyList<LlmModel>>> list, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<LlmModel>> listing;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            listing = Start(connectionId, version, list).Listing;
        }
        return listing.WaitAsync(cancellationToken);
    }

    // Caller owns the gate. The listing is shared, so no single caller's cancellation stops it.
    private Entry Start(string connectionId, string version, Func<CancellationToken, Task<IReadOnlyList<LlmModel>>> list)
    {
        LlmOperationLifetime.Lease operation = _lifetime.Enter(CancellationToken.None);
        var completion = new TaskCompletionSource<IReadOnlyList<LlmModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Entry(version, completion.Task);
        _entries[connectionId] = entry;
        _ = Task.Run(() => CompleteListingAsync(completion, operation, list), CancellationToken.None);
        return entry;
    }

    [SuppressMessage("Design", "CA1031", Justification = "The shared listing preserves provider failures for its waiters and observes abandoned faults.")]
    private async Task CompleteListingAsync(
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
                lock (_gate)
                {
                    if (_disposed) completion.TrySetCanceled(operation.Token);
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

    private sealed record Entry(string Version, Task<IReadOnlyList<LlmModel>> Listing);
}
