using System.Runtime.InteropServices;
using System.Text.Json;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenBackendProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedNativeQueriesDoNotReportAMissingRuntime(bool recordedFailure)
    {
        var original = new InvalidOperationException("private native query cause");
        TensorBackendKind backend = OperatingSystem.IsMacOS() ? TensorBackendKind.Mlx : TensorBackendKind.Cuda;
        var request = new TensorBackendProbeRequest(Guid.NewGuid(), QwenSetup.Definition.Implementation.Id,
            backend, RuntimeInformation.RuntimeIdentifier);

        TensorBackendAvailability observation = QwenBackendProbe.Check(request,
            requested =>
            {
                Assert.Equal(backend, requested);
                throw original;
            }, error =>
            {
                Assert.Same(original, error);
                return recordedFailure;
            }, CancellationToken.None);

        Assert.Equal(recordedFailure ? TensorBackendAvailabilityState.RequiresProcessRestart : TensorBackendAvailabilityState.Unknown,
            observation.State);
        Assert.False(observation.IsAvailable);
        Assert.Null(observation.ActualBackend);
        Assert.Null(observation.IsInitialized);
        if (recordedFailure)
        {
            TensorBackendCandidateDiagnostic diagnostic = Assert.Single(observation.CandidateDiagnostics);
            Assert.True(diagnostic.NativeStateMayRemain);
            Assert.Equal(TensorBackendProbePhase.Initialization, diagnostic.Phase);
            Assert.Equal("runtime-requires-process-exit", diagnostic.Code);
        }
        string json = JsonSerializer.Serialize(observation, TensorJson.BackendAvailability);
        Assert.DoesNotContain(original.Message, json, StringComparison.Ordinal);
    }
}
