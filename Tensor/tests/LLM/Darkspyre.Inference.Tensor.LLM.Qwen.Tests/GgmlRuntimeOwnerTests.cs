using System.Security.Cryptography;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class GgmlRuntimeOwnerTests : IDisposable
{
    private const string Rid = "osx-arm64";
    private static readonly TensorSharpBuildIdentity Build = new("2026.10.11");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darkspyre-ggml-owner-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public async Task ACandidateWithAChangedByteIsRefusedBeforeAnyLibraryLoads()
    {
        Directory.CreateDirectory(_root);
        string entry = Path.Combine(_root, "libGgmlOps.dylib");
        byte[] bytes = "catalogued driver bytes"u8.ToArray();
        await File.WriteAllBytesAsync(entry, bytes);
        var candidate = new TensorRuntimeCandidate(new("ggml:metal", entry, "metal", Build, Rid), TensorBackendKind.GgmlMetal,
            [new("libGgmlOps.dylib", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))], []);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(entry, bytes);
        var native = new FakeNativeLibraries();
        await using var owner = new GgmlRuntimeOwner(new(Build, Rid, [candidate]), native);

        TensorBackendAvailability observation = await owner.CheckAsync(new(Guid.NewGuid(), "test", TensorBackendKind.GgmlMetal, Rid));

        TensorBackendCandidateDiagnostic refusal = Assert.Single(observation.CandidateDiagnostics);
        Assert.Equal(("ggml:metal", TensorBackendProbePhase.Validation, "hash-mismatch"), (refusal.CandidateId, refusal.Phase, refusal.Code));
        Assert.Contains("`libGgmlOps.dylib`", refusal.Message, StringComparison.Ordinal);
        Assert.False(observation.IsAvailable);
        Assert.Empty(native.Loaded);
        Assert.Equal(0, native.Starts);
    }

    [Fact]
    public async Task AutoWithNoCandidateRunsOnManagedCpuButAnExplicitBackendDoesNot()
    {
        var native = new FakeNativeLibraries();
        await using var auto = new GgmlRuntimeOwner(new(Build, Rid, []), native);
        Assert.Equal(TensorBackendKind.Cpu, await auto.AcquireBackendAsync(TensorBackendKind.Auto, CancellationToken.None));

        await using var metal = new GgmlRuntimeOwner(new(Build, Rid, []), native);
        InferenceException failure = await Assert.ThrowsAsync<InferenceException>(() =>
            metal.AcquireBackendAsync(TensorBackendKind.GgmlMetal, CancellationToken.None));
        Assert.Equal(InferenceFailureCode.BackendUnavailable, failure.Code);
        Assert.Equal("No installed driver offers GgmlMetal.", failure.Message);
        Assert.Empty(native.Loaded);
    }
}

/// <summary>Records the native calls a runtime owner makes and loads nothing.</summary>
internal sealed class FakeNativeLibraries : INativeLibraries
{
    public List<string> Loaded { get; } = [];
    public int Starts { get; private set; }
    public IntPtr Load(string path) { Loaded.Add(path); return new IntPtr(Loaded.Count); }
    public void StartGgml(TensorBackendKind backend, IntPtr entry) => Starts++;
    public void StartMlx(string entryPath) => Starts++;
}
