namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Model lists kept for the application's lifetime and shared by every <see cref="LlmProviderService"/>. A
/// connection's list stays until its <see cref="LlmConnection.Version"/> changes or a refresh replaces it.
/// Concurrent requests for one connection share a single listing, and a failed listing is not kept.
/// <see cref="ServiceCollectionExtensions.AddLlmConnect"/> registers one instance.
/// </summary>
public sealed class LlmModelListCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal Task<IReadOnlyList<LlmModel>> GetAsync(string connectionId, string version,
        Func<Task<IReadOnlyList<LlmModel>>> list, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<LlmModel>> listing;
        lock (_gate)
        {
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
        Func<Task<IReadOnlyList<LlmModel>>> list, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<LlmModel>> listing;
        lock (_gate) { listing = Start(connectionId, version, list).Listing; }
        return listing.WaitAsync(cancellationToken);
    }

    // Caller owns the gate. The listing is shared, so no single caller's cancellation stops it.
    private Entry Start(string connectionId, string version, Func<Task<IReadOnlyList<LlmModel>>> list)
    {
        var entry = new Entry(version, Task.Run(list, CancellationToken.None));
        _entries[connectionId] = entry;
        return entry;
    }

    private sealed record Entry(string Version, Task<IReadOnlyList<LlmModel>> Listing);
}
