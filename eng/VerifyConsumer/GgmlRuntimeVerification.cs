using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.LLM.Qwen;
using TensorSharp.GGML;
using Darkspyre.Inference.Tensor.Ggml;

internal static class GgmlRuntimeVerification
{
    internal static async Task RunAsync(string mode)
    {
        if (!OperatingSystem.IsMacOS() || GgmlNativeLoader.RuntimeIdentifier != "osx-arm64")
            throw new PlatformNotSupportedException("The explicit native-package check requires macOS ARM64.");
        if (GgmlNativeLoader.State != GgmlRuntimeState.Unconfigured)
            throw new InvalidOperationException("Contract discovery initialized GGML native state.");

        IReadOnlyList<GgmlNativeCandidate> candidates = await GgmlNativeLoader.ResolvePackageCandidatesAsync();
        if (mode == "absent" && candidates.Count != 0 || mode != "absent" && candidates.Count != 2)
            throw new InvalidOperationException("The Mac package must expose Metal and CPU candidates, or none in absent mode.");
        foreach (GgmlNativeCandidate candidate in candidates)
        {
            if (candidate.TensorSharpBuild != GgmlNativeLoader.TensorSharpBuild
                || candidate.NativeAbi != GgmlNativeLoader.NativeAbi || candidate.Rid != "osx-arm64"
                || candidate.Variant != "metal" || GgmlNativeLoader.Check(candidate) is not null)
                throw new InvalidOperationException("The deployed Mac package failed exact identity or file validation.");
        }
        if (GgmlNativeLoader.State != GgmlRuntimeState.Unconfigured)
            throw new InvalidOperationException("Catalog resolution loaded or initialized a native backend.");

        TensorBackendKind requested = mode switch
        {
            "cpu" => TensorBackendKind.GgmlCpu,
            "metal" => TensorBackendKind.GgmlMetal,
            _ => TensorBackendKind.Auto
        };
        await using GgmlRuntimeOwner owner = GgmlRuntimeOwner.GetOrCreate();
        TensorBackendAvailability observation = await owner.CheckAsync(new(Guid.NewGuid(),
            QwenSetup.Definition.Implementation.Id, requested, "osx-arm64", owner.Plan.Build));
        if (mode == "absent")
        {
            if (observation.State != TensorBackendAvailabilityState.Unavailable
                || observation.ActualBackend is not null || owner.Current is not null
                || GgmlNativeLoader.State != GgmlRuntimeState.Unconfigured)
                throw new InvalidOperationException("Missing native packages did not refuse without initialization.");
        }
        else
        {
            TensorRuntimeBackendIdentity identity = owner.Current
                ?? throw new InvalidOperationException("The package did not initialize a GGML backend.");
            if (!observation.IsAvailable || observation.IsInitialized != true
                || observation.ActualBackend != identity.Backend || observation.TensorSharpBuild != owner.Plan.Build
                || identity.Build != owner.Plan.Build || identity.TargetRid != "osx-arm64" || identity.Variant != "metal"
                || string.IsNullOrWhiteSpace(identity.NativeSourceCommit) || string.IsNullOrWhiteSpace(identity.CandidateId)
                || requested != TensorBackendKind.Auto && identity.Backend != requested
                || identity.Backend is not (TensorBackendKind.GgmlCpu or TensorBackendKind.GgmlMetal))
                throw new InvalidOperationException("The initialized backend does not match the package/runtime plan.");
        }

        TensorRuntimeShutdownResult shutdown = await owner.ShutdownAsync();
        if (shutdown.State != TensorRuntimeShutdownState.Completed || shutdown.OutstandingLeases != 0
            || shutdown.NativeStateMayRemain || owner.State != TensorRuntimeState.Stopped)
            throw new InvalidOperationException("The package runtime did not shut down cleanly.");
        if ((await owner.ShutdownAsync()).State != TensorRuntimeShutdownState.AlreadyStopped)
            throw new InvalidOperationException("Repeated shutdown did not report terminal completion.");
        Console.WriteLine($"GGML {mode}: {observation.State}; actual backend {observation.ActualBackend}; "
            + $"build {owner.Plan.Build.Version}; ABI {owner.Plan.Build.NativeAbi}; shutdown {shutdown.State}.");
    }
}
