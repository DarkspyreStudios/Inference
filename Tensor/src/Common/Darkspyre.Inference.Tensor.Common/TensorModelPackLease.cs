using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Common;

public sealed class TensorModelPackLease : IAsyncDisposable
{
    private Func<ValueTask>? _release;

    internal TensorModelPackLease(
        TensorModelKey key,
        IReadOnlyDictionary<string, PersistenceFileReference> artifacts,
        Func<ValueTask> release)
    {
        Key = key;
        Artifacts = artifacts;
        _release = release;
    }

    public TensorModelKey Key { get; }

    public IReadOnlyDictionary<string, PersistenceFileReference> Artifacts { get; }

    public PersistenceFileReference GetRequiredArtifact(string role) =>
        Artifacts.TryGetValue(role, out PersistenceFileReference? artifact)
            ? artifact
            : throw new KeyNotFoundException($"Model pack '{Key}' has no artifact role '{role}'.");

    public ValueTask DisposeAsync()
    {
        Func<ValueTask>? release = Interlocked.Exchange(ref _release, null);
        return release is null ? ValueTask.CompletedTask : release();
    }
}
