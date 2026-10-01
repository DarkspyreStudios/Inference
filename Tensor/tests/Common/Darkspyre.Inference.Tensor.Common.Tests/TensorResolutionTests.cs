namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorResolutionTests
{
    private static readonly TensorModelCandidate Candidate = new("test", new("owner/model", new('1', 40), "weights.bin"),
        new("test", null, null), "test-license", false, false, []);

    [Fact]
    public void FingerprintIsStableForOrderingAndChangesForVariantCompanionBackendOrCapability()
    {
        TensorModelArtifactDefinition[] artifacts = [Artifact("model"), Artifact("companion")];
        TensorModelPackDefinition original = Pack(artifacts);
        Assert.Equal(original.Key, Pack(artifacts.Reverse().ToArray()).Key);
        Assert.Equal(original.Key, Pack(artifacts.Select(item => item with { Sources = item.Sources.Reverse().ToArray() }).ToArray()).Key);
        Assert.NotEqual(original.Key, Pack([artifacts[0], artifacts[1] with { Sha256 = new('b', 64) }]).Key);
        Assert.NotEqual(original.Key, TensorResolvedPack.Create(Candidate with { Selection = Candidate.Selection with { Variant = "another.bin" } },
            TensorCapability.Image, "test", original.SupportedBackends, artifacts).Key);
        Assert.NotEqual(original.Key, TensorResolvedPack.Create(Candidate, TensorCapability.Image, "test",
            new HashSet<TensorBackendKind> { TensorBackendKind.Cuda }, artifacts).Key);
        Assert.NotEqual(original.Key, TensorResolvedPack.Create(Candidate, TensorCapability.Llm, "test", original.SupportedBackends, artifacts).Key);
        var catalog = new TensorModelCatalog([original]);
        catalog.Register(Pack(artifacts.Reverse().Select(item => item with { Sources = item.Sources.Reverse().ToArray() }).ToArray()));
        Assert.Single(catalog.Definitions);
    }

    [Fact]
    public void ExactBuiltinRetainsKeyAndProvenanceButOtherManifestsCannotReuseIt()
    {
        TensorModelArtifactDefinition[] artifacts = [Artifact("model")];
        TensorModelPackDefinition builtIn = Pack(artifacts) with { Key = new("builtin", "old-key") };
        TensorModelPackDefinition resolved = TensorResolvedPack.Create(Candidate, TensorCapability.Image, "test", builtIn.SupportedBackends, artifacts, builtIn);
        Assert.Equal(builtIn.Key, resolved.Key);
        Assert.Equal(artifacts[0].Provenance, resolved.Artifacts[0].Provenance);
        Assert.NotEqual(builtIn.Key, TensorResolvedPack.Create(Candidate, TensorCapability.Image, "test", builtIn.SupportedBackends,
            [artifacts[0] with { Provenance = artifacts[0].Provenance! with { Revision = new('2', 40) } }], builtIn).Key);
    }

    [Fact]
    public void ResolutionSnapshotsInputsAndRequiresLicenseAndIntegrity()
    {
        TensorModelArtifactDefinition[] artifacts = [Artifact("model")];
        TensorModelPackDefinition resolved = Pack(artifacts);
        artifacts[0] = artifacts[0] with { Sha256 = new('b', 64) };
        Assert.Equal(new('a', 64), resolved.Artifacts[0].Sha256);
        Assert.Throws<ArgumentException>(() => Pack([Artifact("model") with { Provenance = null }]));
        Assert.Throws<ArgumentException>(() => Pack([Artifact("model") with { Sha256 = "unknown" }]));
        Assert.Throws<ArgumentException>(() => TensorResolvedPack.Create(Candidate with { License = null }, TensorCapability.Image,
            "test", resolved.SupportedBackends, [Artifact("model")]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CpuCudaProbeReportsActualAvailabilityWithoutProbingOnConstruction(bool available)
    {
        int calls = 0;
        ITensorBackendProbe probe = TensorBackendProbes.CpuCuda(() => { calls++; return available; });
        Assert.Equal(0, calls);
        Assert.True((await probe.CheckAsync(Request(TensorBackendKind.Auto))).IsAvailable);
        Assert.True((await probe.CheckAsync(Request(TensorBackendKind.Cpu))).IsAvailable);
        Assert.Equal(0, calls);
        Assert.Equal(available, (await probe.CheckAsync(Request(TensorBackendKind.Cuda))).IsAvailable);
        Assert.Equal(1, calls);
        Assert.False((await probe.CheckAsync(Request(TensorBackendKind.Mlx))).IsAvailable);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckAsync(Request(TensorBackendKind.Cuda), new(true)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BackendProbeDistinguishesMissingNativeRuntimeFromUnknownStateWithoutExceptionDisclosure()
    {
        var unavailable = TensorBackendProbes.CpuCuda(() => throw new DllNotFoundException("private runtime path"));
        var unknown = TensorBackendProbes.CpuCuda(() => throw new IOException("private runtime path"));
        TensorBackendAvailability missing = await unavailable.CheckAsync(Request(TensorBackendKind.Cuda));
        TensorBackendAvailability undecided = await unknown.CheckAsync(Request(TensorBackendKind.Cuda));
        Assert.False(missing.IsAvailable);
        Assert.Equal(TensorBackendAvailabilityState.Unknown, undecided.State);
        Assert.DoesNotContain("private runtime path", missing.Reason + undecided.Reason, StringComparison.Ordinal);
    }

    private static TensorModelPackDefinition Pack(IReadOnlyList<TensorModelArtifactDefinition> artifacts) =>
        TensorResolvedPack.Create(Candidate, TensorCapability.Image, "test", new HashSet<TensorBackendKind> { TensorBackendKind.Cpu }, artifacts);

    private static TensorBackendProbeRequest Request(TensorBackendKind backend) =>
        new(Guid.NewGuid(), "test", backend, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);

    private static TensorModelArtifactDefinition Artifact(string role) => new()
    {
        Role = role,
        RelativePath = role + ".bin",
        Length = 10,
        Sha256 = new('a', 64),
        Sources = [new("https://example.test/z"), new("https://example.test/a")],
        Provenance = new(new("https://example.test/"), "owner/model", new('1', 40), role + ".bin", "test-license")
    };
}
