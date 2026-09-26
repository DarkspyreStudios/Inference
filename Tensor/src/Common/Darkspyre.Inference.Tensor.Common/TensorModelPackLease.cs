namespace Darkspyre.Inference.Tensor.Common;

public sealed class TensorModelPackLease : IAsyncDisposable
{
    private Func<ValueTask>? _release;

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

    public ValueTask DisposeAsync()
    {
        Func<ValueTask>? release = Interlocked.Exchange(ref _release, null);
        return release is null ? ValueTask.CompletedTask : release();
    }
}
