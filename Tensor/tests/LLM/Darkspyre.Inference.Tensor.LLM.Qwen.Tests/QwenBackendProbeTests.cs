using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenBackendProbeTests
{
    [Fact]
    public async Task CpuPrerequisiteCheckDoesNotRequireNativeGpuLibrariesOrAResidentModel()
    {
        TensorBackendAvailability result = await new QwenBackendProbe().CheckAsync(Request(TensorBackendKind.Cpu));
        Assert.True(result.IsAvailable);
        Assert.Equal(TensorBackendKind.Cpu, result.RequestedBackend);
        Assert.Null(result.ActualBackend);
        Assert.Null(result.IsInitialized);
        Assert.Contains("allocation is not guaranteed", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationPreventsNativeInitializationAndUnknownBackendIsUnavailable()
    {
        var probe = new QwenBackendProbe();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckAsync(Request(TensorBackendKind.Auto), new(true)));
        Assert.False((await probe.CheckAsync(Request((TensorBackendKind)999))).IsAvailable);
    }

    private static TensorBackendProbeRequest Request(TensorBackendKind backend) =>
        new(Guid.NewGuid(), QwenSetup.Definition.Implementation.Id, backend, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitNativeCandidatesDoNotFallThroughToAmbientGgml(bool empty)
    {
        TensorBackendProbeRequest original = Request(TensorBackendKind.GgmlCpu);
        var build = new TensorSharpBuildIdentity("package-version", new('a', 64), new('b', 40));
        TensorNativeCandidate[] candidates = empty ? [] : [new("candidate", "/private/native/bridge", "cpu", build, original.TargetRid)];
        var request = new TensorBackendProbeRequest(original.OperationId, original.ImplementationId, original.RequestedBackend,
            original.TargetRid, build, candidates);
        TensorBackendAvailability result = await new QwenBackendProbe().CheckAsync(request);
        Assert.Equal(empty ? TensorBackendAvailabilityState.Unavailable : TensorBackendAvailabilityState.Unknown, result.State);
        Assert.Null(result.ActualBackend);
        Assert.Null(result.LoadedCandidateId);
        Assert.Null(result.IsInitialized);
        Assert.DoesNotContain("private", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongImplementationIsUnsupportedBeforeBackendEntry()
    {
        var request = new TensorBackendProbeRequest(Guid.NewGuid(), "another", TensorBackendKind.GgmlCpu,
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
        Assert.Equal(TensorBackendAvailabilityState.Unsupported, (await new QwenBackendProbe().CheckAsync(request)).State);
    }

    [Fact]
    public void ProbeAndLoaderShareAutomaticBackendPolicy()
    {
        string rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        TensorBackendKind[] expected = rid switch
        {
            "osx-arm64" => [TensorBackendKind.GgmlMetal, TensorBackendKind.GgmlCpu],
            "win-arm64" => [TensorBackendKind.GgmlVulkan, TensorBackendKind.GgmlCpu],
            "linux-x64" or "linux-arm64" or "win-x64" => [TensorBackendKind.GgmlCuda, TensorBackendKind.GgmlVulkan, TensorBackendKind.GgmlCpu],
            _ => []
        };
        Assert.Equal(expected,
            GgmlRuntimeOwner.CompatibleBackends(TensorBackendKind.Auto, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier));
        Assert.Equal(new[] { TensorBackendKind.Cpu },
            GgmlRuntimeOwner.CompatibleBackends(TensorBackendKind.Cpu, System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier));
    }
}
