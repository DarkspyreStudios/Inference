using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenBackendProbeTests
{
    [Fact]
    public async Task CpuPrerequisiteCheckDoesNotRequireNativeGpuLibrariesOrAResidentModel()
    {
        TensorBackendAvailability result = await new QwenBackendProbe().CheckAsync(TensorBackendKind.Cpu);
        Assert.True(result.IsAvailable);
        Assert.Equal(TensorBackendKind.Cpu, result.Backend);
        Assert.Contains("allocation is not guaranteed", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationPreventsNativeInitializationAndUnknownBackendIsUnavailable()
    {
        var probe = new QwenBackendProbe();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckAsync(TensorBackendKind.Auto, new(true)));
        Assert.False((await probe.CheckAsync((TensorBackendKind)999)).IsAvailable);
    }

    [Fact]
    public void ProbeAndLoaderShareAutomaticBackendPolicy()
    {
        Assert.Equal(OperatingSystem.IsMacOS() ? TensorBackendKind.GgmlMetal : TensorBackendKind.GgmlCuda,
            QwenLlmInference.ResolveBackend(TensorBackendKind.Auto));
        Assert.Equal(TensorBackendKind.Cpu, QwenLlmInference.ResolveBackend(TensorBackendKind.Cpu));
    }
}
