using System.Runtime.InteropServices;
using System.Text.Json;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorBackendProbeContractTests
{
    private static readonly TensorSharpBuildIdentity Build = new("package-version", new('a', 64), new('b', 40));
    private static readonly TensorNativeCandidate First = new("first", "/private/configured/first.so", "cpu", Build, "linux-x64");
    private static readonly TensorNativeCandidate Second = new("second", "/private/configured/second.so", "cpu", Build, "linux-x64");

    [Fact]
    public void RequestsSnapshotOrderedCandidatesAndKeepNullSeparateFromEmpty()
    {
        TensorNativeCandidate[] candidates = [First, Second];
        TensorBackendProbeRequest request = Request(candidates: candidates);
        candidates[0] = Second;
        Assert.Equal([First, Second], request.NativeCandidates);
        Assert.Null(Request().NativeCandidates);
        Assert.Empty(Request(candidates: []).NativeCandidates!);
        Assert.Contains("\"NativeCandidates\":null", JsonSerializer.Serialize(Request(), TensorJson.BackendProbeRequest), StringComparison.Ordinal);
        Assert.Contains("\"NativeCandidates\":[]", JsonSerializer.Serialize(Request(candidates: []), TensorJson.BackendProbeRequest), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new TensorBackendProbeRequest(Guid.Empty, "test", TensorBackendKind.Cuda, "linux-x64"));
        Assert.Throws<ArgumentException>(() => new TensorBackendProbeRequest(Guid.NewGuid(), "", TensorBackendKind.Cuda, "linux-x64"));
        Assert.Throws<ArgumentException>(() => new TensorBackendProbeRequest(Guid.NewGuid(), "test", TensorBackendKind.Cuda, ""));
    }

    [Theory]
    [InlineData(TensorBackendAvailabilityState.Unknown, false)]
    [InlineData(TensorBackendAvailabilityState.Unsupported, false)]
    [InlineData(TensorBackendAvailabilityState.Unavailable, false)]
    [InlineData(TensorBackendAvailabilityState.Available, true)]
    [InlineData(TensorBackendAvailabilityState.RequiresProcessRestart, false)]
    public void AvailabilityDerivesOnlyFromState(TensorBackendAvailabilityState state, bool available)
    {
        TensorBackendProbeRequest request = Request();
        TensorBackendAvailability observation = TensorBackendAvailability.Observe(request, state, "observation");
        Assert.Equal(available, observation.IsAvailable);
        Assert.Equal(request.OperationId, observation.OperationId);
        Assert.Equal(request.ImplementationId, observation.ImplementationId);
        Assert.Equal(request.RequestedBackend, observation.RequestedBackend);
        Assert.Null(observation.ActualBackend);
        Assert.Null(observation.IsInitialized);
        Assert.Empty(observation.DetectedDeviceCapabilities);
        Assert.Empty(observation.CandidatePresence);
    }

    [Fact]
    public void OwnedMetadataPreservesBuildFieldsAndIndependentObservationEvidence()
    {
        TensorBackendProbeRequest request = Request(candidates: [First, Second]);
        TensorBackendProbeRequest restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, TensorJson.BackendProbeRequest), TensorJson.BackendProbeRequest)!;
        Assert.Equal(request.OperationId, restored.OperationId);
        Assert.Equal(Build, restored.ExpectedTensorSharpBuild);
        Assert.Equal([First, Second], restored.NativeCandidates);
        TensorBackendCandidateDiagnostic[] diagnostics = [new("first", TensorBackendProbePhase.Validation, "refused", "incompatible"),
            new("second", TensorBackendProbePhase.Initialization, "failed", "initialization failed", true)];
        TensorNativeCandidatePresence[] presence = [new("first", true), new("second", true)];
        var observation = new TensorBackendAvailability(request.OperationId, "test", TensorBackendKind.Cuda,
            TensorBackendAvailabilityState.RequiresProcessRestart, "restart required", DateTimeOffset.UnixEpoch, request.TargetRid,
            tensorSharpBuild: Build, loadedCandidateId: "second", isInitialized: false,
            detectedDeviceCapabilities: [new(TensorBackendKind.Cuda, true)], candidatePresence: presence, candidateDiagnostics: diagnostics);
        diagnostics[0] = diagnostics[1];
        presence[0] = new("first", false);
        string json = JsonSerializer.Serialize(observation, TensorJson.BackendAvailability);
        TensorBackendAvailability result = JsonSerializer.Deserialize(json.Replace("\"IsAvailable\":false", "\"IsAvailable\":true", StringComparison.Ordinal), TensorJson.BackendAvailability)!;
        Assert.False(result.IsAvailable);
        Assert.False(result.IsInitialized);
        Assert.Null(result.ActualBackend);
        Assert.True(Assert.Single(result.DetectedDeviceCapabilities).IsAvailable);
        Assert.True(result.CandidatePresence[0].IsInstalled);
        Assert.Equal("first", result.CandidateDiagnostics[0].CandidateId);
        Assert.True(result.CandidateDiagnostics[1].NativeStateMayRemain);
        Assert.Equal(Build.NativeAbi, result.TensorSharpBuild!.NativeAbi);
        Assert.Equal(Build.GgmlCommit, result.TensorSharpBuild.GgmlCommit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitNativePlansNeverInvokeAmbientCudaProbe(bool empty)
    {
        int nativeCalls = 0;
        ITensorBackendProbe probe = TensorBackendProbes.CpuCuda(() => { nativeCalls++; return true; });
        TensorBackendProbeRequest request = Request(empty ? [] : [First, Second]);
        TensorBackendAvailability result = await probe.CheckAsync(request);
        Assert.Equal(empty ? TensorBackendAvailabilityState.Unavailable : TensorBackendAvailabilityState.Unknown, result.State);
        Assert.Equal(0, nativeCalls);
        Assert.DoesNotContain("private", result.Reason, StringComparison.Ordinal);
        if (!empty) Assert.Equal(["first", "second"], result.CandidateDiagnostics.Select(item => item.CandidateId));
    }

    [Fact]
    public async Task ExpectedNativeBuildNeverFallsBackToUnboundAmbientProbe()
    {
        int nativeCalls = 0;
        ITensorBackendProbe probe = TensorBackendProbes.CpuCuda(() => { nativeCalls++; return true; });
        TensorBackendAvailability result = await probe.CheckAsync(Request(expectedBuild: Build));
        Assert.Equal(TensorBackendAvailabilityState.Unknown, result.State);
        Assert.Equal(0, nativeCalls);
        Assert.Null(result.TensorSharpBuild);
    }

    [Fact]
    public async Task ForeignRidAndUnsupportedBackendNeverInvokeNativeProbe()
    {
        int nativeCalls = 0;
        ITensorBackendProbe probe = TensorBackendProbes.CpuCuda(() => { nativeCalls++; return true; });
        Assert.Equal(TensorBackendAvailabilityState.Unsupported, (await probe.CheckAsync(new(Guid.NewGuid(), "test", TensorBackendKind.Cuda, "another-rid"))).State);
        Assert.Equal(TensorBackendAvailabilityState.Unsupported, (await probe.CheckAsync(Request(backend: TensorBackendKind.CoreMl))).State);
        Assert.Equal(0, nativeCalls);
    }

    [Fact]
    public async Task CancellationDuringNativeCheckCannotPublishAvailable()
    {
        using var cancellation = new CancellationTokenSource();
        ITensorBackendProbe probe = TensorBackendProbes.CpuCuda(() => { cancellation.Cancel(); return true; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.CheckAsync(Request(), cancellation.Token));
    }

    private static TensorBackendProbeRequest Request(IReadOnlyList<TensorNativeCandidate>? candidates = null,
        TensorBackendKind backend = TensorBackendKind.Cuda, TensorSharpBuildIdentity? expectedBuild = null) =>
        new(Guid.NewGuid(), "test", backend, RuntimeInformation.RuntimeIdentifier, expectedBuild ?? (candidates is null ? null : Build), candidates);

    [Theory]
    [InlineData(TensorBackendKind.GgmlCpu, false)]
    [InlineData(TensorBackendKind.GgmlCpu, null)]
    [InlineData(TensorBackendKind.Auto, true)]
    [InlineData(null, true)]
    public void ActualBackendCannotRepresentAnUninitializedSuggestion(TensorBackendKind? actual, bool? initialized) =>
        Assert.Throws<ArgumentException>(() => new TensorBackendAvailability(Guid.NewGuid(), "test", TensorBackendKind.Auto,
            TensorBackendAvailabilityState.Available, "observation", DateTimeOffset.UnixEpoch, "linux-x64",
            actualBackend: actual, isInitialized: initialized));
}
