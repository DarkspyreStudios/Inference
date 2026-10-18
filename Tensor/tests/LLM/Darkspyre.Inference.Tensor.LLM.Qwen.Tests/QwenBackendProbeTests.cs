using System.Runtime.InteropServices;
using System.Text.Json;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenBackendProbeTests
{
    [Fact]
    public void FailedNativeQueriesDoNotReportAMissingRuntime()
    {
        var original = new InvalidOperationException("private native query cause");
        // The managed CUDA backend is the local check's native query; macOS refuses it first, so the CPU query stands in.
        TensorBackendKind backend = OperatingSystem.IsMacOS() ? TensorBackendKind.Cpu : TensorBackendKind.Cuda;
        var request = new TensorBackendProbeRequest(Guid.NewGuid(), QwenSetup.Definition.Implementation.Id,
            backend, RuntimeInformation.RuntimeIdentifier);

        TensorBackendAvailability observation = QwenBackendProbe.Check(request,
            requested =>
            {
                Assert.Equal(backend, requested);
                throw original;
            }, CancellationToken.None);

        Assert.Equal(TensorBackendAvailabilityState.Unknown, observation.State);
        Assert.False(observation.IsAvailable);
        Assert.Null(observation.ActualBackend);
        Assert.Null(observation.IsInitialized);
        string json = JsonSerializer.Serialize(observation, TensorJson.BackendAvailability);
        Assert.DoesNotContain(original.Message, json, StringComparison.Ordinal);
    }
}
