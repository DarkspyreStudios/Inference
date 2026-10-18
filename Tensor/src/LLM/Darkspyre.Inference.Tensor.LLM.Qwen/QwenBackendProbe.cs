using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Cuda;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

internal sealed class QwenBackendProbe(GgmlRuntimeOwner? runtimeOwner = null) : ITensorBackendProbe
{
    // Loading a native library or runtime to answer is synchronous work.
    public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (TensorBackendProbes.ValidateLocalRequest(request, QwenSetup.Definition.Implementation.Id) is { } invalid)
            return Task.FromResult(invalid);
        if (GgmlRuntimeOwner.IsNative(request.RequestedBackend))
            return (runtimeOwner ?? GgmlRuntimeOwner.GetOrCreate()).CheckAsync(request, cancellationToken);
        return Task.Run(() => Check(request, IsBackendAvailable, cancellationToken), cancellationToken);
    }

    internal static TensorBackendAvailability Check(TensorBackendProbeRequest request,
        Func<TensorBackendKind, bool> isBackendAvailable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TensorBackendProbes.ValidateLocalRequest(request, QwenSetup.Definition.Implementation.Id) is { } invalid) return invalid;
        TensorBackendKind actual = request.RequestedBackend;
        if (actual == TensorBackendKind.CoreMl
            || !OperatingSystem.IsMacOS() && actual is TensorBackendKind.GgmlMetal
            || OperatingSystem.IsMacOS() && actual is TensorBackendKind.Cuda or TensorBackendKind.GgmlCuda or TensorBackendKind.GgmlVulkan)
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported, "This backend is unsupported by Qwen on the target platform.");
        if (actual != TensorBackendKind.Cpu && TensorBackendProbes.RejectUnboundNativePlan(request) is { } unbound) return unbound;
        bool? available;
        try
        {
            available = isBackendAvailable(actual);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException)
        { available = false; }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        { available = null; }
        cancellationToken.ThrowIfCancellationRequested();
        return TensorBackendAvailability.Observe(request, available switch
        {
            true => TensorBackendAvailabilityState.Available,
            false => TensorBackendAvailabilityState.Unavailable,
            null => TensorBackendAvailabilityState.Unknown
        },
            available == true ? $"{actual} runtime prerequisites are available. Model allocation is not guaranteed."
                : available == false ? $"{actual} runtime/device prerequisites are unavailable." : $"{actual} availability is unknown.",
            actual is TensorBackendKind.Cpu or TensorBackendKind.Cuda ? [new(actual, available)] : []);
    }

    private static bool IsBackendAvailable(TensorBackendKind backend) => backend switch
    {
        TensorBackendKind.Cpu => true,
        TensorBackendKind.Cuda => CudaBackend.IsAvailable(),
        _ => false
    };
}
