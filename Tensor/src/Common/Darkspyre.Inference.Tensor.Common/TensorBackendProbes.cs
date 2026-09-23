namespace Darkspyre.Inference.Tensor.Common;

/// <summary>Shared CPU/CUDA policy for direct Tensor engines. Creating a probe never invokes native code.</summary>
public static class TensorBackendProbes
{
    public static ITensorBackendProbe CpuCuda(Func<bool> isCudaAvailable)
    {
        ArgumentNullException.ThrowIfNull(isCudaAvailable);
        return new CpuCudaProbe(isCudaAvailable);
    }

    private sealed class CpuCudaProbe(Func<bool> isCudaAvailable) : ITensorBackendProbe
    {
        public Task<TensorBackendAvailability> CheckAsync(TensorBackendKind backend, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            { available = false; reason = "The native CUDA runtime is unavailable on this machine."; }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            { available = null; reason = "Backend availability could not be determined."; }
            return Task.FromResult(new TensorBackendAvailability(backend, available, reason, DateTimeOffset.UtcNow));
        }
    }
}
