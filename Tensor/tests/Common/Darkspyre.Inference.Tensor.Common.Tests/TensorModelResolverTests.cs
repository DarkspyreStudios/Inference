namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorModelResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darkspyre-resolver-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OptionalCompanionIsOmittedUnlessALocationSuppliesIt()
    {
        Directory.CreateDirectory(_root);
        string primary = Path.Combine(_root, "model.bin"), companion = Path.Combine(_root, "vision.bin");
        await File.WriteAllBytesAsync(primary, [1, 2, 3]);
        await File.WriteAllBytesAsync(companion, [4, 5, 6]);
        var resolver = new Resolver();
        var selection = new TensorModelSelection(new TensorModelLocation.LocalFile(primary));
        TensorSourceResult<TensorModelCandidate> without = await resolver.DiscoverAsync(selection);
        Assert.Equal(TensorSourceState.Available, without.State);
        Assert.Equal("model", Assert.Single(without.Value!.ArtifactRoles).Key);
        Assert.Single(without.Value.Artifacts);

        TensorSourceResult<TensorModelCandidate> with = await resolver.DiscoverAsync(selection with
        { Companions = [new("vision", new TensorModelLocation.LocalFile(companion))] });
        Assert.Equal(TensorSourceState.Available, with.State);
        Assert.Equal(2, with.Value!.Artifacts.Count);
        Assert.Equal("vision/vision.bin", with.Value.ArtifactRoles["vision"]);
        Assert.Contains(with.Value.Artifacts, artifact => artifact.Path == with.Value.ArtifactRoles["vision"]);
    }

    private sealed class Resolver : TensorModelResolver
    {
        public override IReadOnlyList<TensorArtifactRequirement> Requirements =>
            [new("model", "*.bin"), new("vision", string.Empty, IsOptional: true)];
        public override Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend,
            ITensorArtifactReader reader, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
