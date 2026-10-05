namespace Darkspyre.Inference.Tensor.Common;

public sealed class TensorModelPackLease : IAsyncDisposable
{
    private readonly object _gate = new();
    private Func<ValueTask>? _release;
    private Task? _releaseTask;

    internal TensorModelPackLease(
        TensorModelKey key,
        IReadOnlyDictionary<string, string> artifacts,
        Func<ValueTask> release)
    {
        Key = key;
        Artifacts = artifacts;
        _release = release;
    }

    public TensorModelKey Key { get; }

    /// <summary>The absolute file path of each artifact, by role.</summary>
    public IReadOnlyDictionary<string, string> Artifacts { get; }

    /// <summary>Returns the absolute file path of the artifact with <paramref name="role"/>.</summary>
    public string GetRequiredArtifact(string role) =>
        Artifacts.TryGetValue(role, out string? artifact)
            ? artifact
            : throw new KeyNotFoundException($"Model pack '{Key}' has no artifact role '{role}'.");

    /// <summary>Releases the artifact lease once. Concurrent and later callers observe the same completion or failure.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new(_releaseTask ??= ReleaseAsync(_release!));
        }
    }

    private async Task ReleaseAsync(Func<ValueTask> release)
    {
        await release().ConfigureAwait(false);
        _release = null;
    }
}
