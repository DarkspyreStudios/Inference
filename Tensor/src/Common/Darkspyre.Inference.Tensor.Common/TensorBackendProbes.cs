using System.Runtime.InteropServices;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Shared CPU/CUDA policy for direct Tensor engines. Creating a probe never invokes native code.</summary>
public static class TensorBackendProbes
{
    /// <summary>Returns an unsupported result when the request names an undefined backend, another implementation or another runtime identifier; otherwise returns null.</summary>
    public static TensorBackendAvailability? ValidateLocalRequest(TensorBackendProbeRequest request, string? implementationId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.RequestedBackend))
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported, "The requested backend is unsupported.");
        if (implementationId is not null && request.ImplementationId != implementationId)
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported, "The probe belongs to another implementation.");
        if (request.TargetRid != RuntimeInformation.RuntimeIdentifier)
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported, "The local probe cannot execute another target RID.");
        return null;
    }

    /// <summary>The local default does not interpret a consumer's explicit native plan as ambient authority.</summary>
    public static TensorBackendAvailability? RejectUnboundNativePlan(TensorBackendProbeRequest request)
    {
        if (request.NativeCandidates is { Count: 0 })
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unavailable,
                "The runtime plan supplies no installed native candidates.");
        if (request.NativeCandidates is not null || request.ExpectedTensorSharpBuild is not null)
        {
            const string message = "An explicitly bound runtime probe is required to execute this native plan.";
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unknown, message,
                candidateDiagnostics: request.NativeCandidates?.Select(candidate => new TensorBackendCandidateDiagnostic(
                    candidate.Id, TensorBackendProbePhase.Validation, "backend-bound-probe-required", message)).ToArray()
                    ?? [new(null, TensorBackendProbePhase.Validation, "backend-bound-probe-required", message)]);
        }
        return null;
    }

    /// <summary>Creates a probe that offers the CPU backend and the CUDA backend when <paramref name="isCudaAvailable"/> returns true.</summary>
    public static ITensorBackendProbe CpuCuda(Func<bool> isCudaAvailable)
    {
        ArgumentNullException.ThrowIfNull(isCudaAvailable);
        return new CpuCudaProbe(isCudaAvailable);
    }

    private sealed class CpuCudaProbe(Func<bool> isCudaAvailable) : ITensorBackendProbe
    {
        // Loading a native library or runtime to answer is synchronous work.
        public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request, CancellationToken cancellationToken = default) =>
            Task.Run(() => Check(request, cancellationToken), cancellationToken);

        private TensorBackendAvailability Check(TensorBackendProbeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ValidateLocalRequest(request) is { } invalid) return invalid;
            TensorBackendKind backend = request.RequestedBackend;
            if (backend is not (TensorBackendKind.Auto or TensorBackendKind.Cpu or TensorBackendKind.Cuda))
                return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported, "The backend is unsupported by this CPU/CUDA adapter.");
            if (backend == TensorBackendKind.Cuda && RejectUnboundNativePlan(request) is { } unbound) return unbound;
            bool? available;
            string reason;
            try
            {
                available = backend switch
                {
                    TensorBackendKind.Auto or TensorBackendKind.Cpu => true,
                    TensorBackendKind.Cuda => isCudaAvailable(),
                    _ => false
                };
                reason = backend switch
                {
                    TensorBackendKind.Auto => "Automatic selection can fall back to the managed CPU backend. Memory allocation is not guaranteed.",
                    TensorBackendKind.Cpu => "The managed CPU backend is available. Memory allocation is not guaranteed.",
                    TensorBackendKind.Cuda when available == true => "A CUDA device and driver were detected. Model allocation is not guaranteed.",
                    _ => "The selected backend is unsupported or its runtime/device is unavailable."
                };
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException)
            { available = false; reason = $"The native CUDA runtime is unavailable on this machine: {exception.Message}"; }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            { available = null; reason = $"Backend availability could not be determined: {exception.Message}"; }
            cancellationToken.ThrowIfCancellationRequested();
            return TensorBackendAvailability.Observe(request, available switch
            {
                true => TensorBackendAvailabilityState.Available,
                false => TensorBackendAvailabilityState.Unavailable,
                null => TensorBackendAvailabilityState.Unknown
            }, reason, [new(backend == TensorBackendKind.Auto ? TensorBackendKind.Cpu : backend, available)]);
        }
    }
}
