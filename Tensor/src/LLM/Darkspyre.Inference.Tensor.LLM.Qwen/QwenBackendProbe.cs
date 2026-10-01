using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Cuda;
using TensorSharp.GGML;
using TensorSharp.MLX;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

internal sealed class QwenBackendProbe : ITensorBackendProbe
{
    // Loading a native library or runtime to answer is synchronous work.
    public Task<TensorBackendAvailability> CheckAsync(TensorBackendProbeRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => Check(request, cancellationToken), cancellationToken);

    private static TensorBackendAvailability Check(TensorBackendProbeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TensorBackendProbes.ValidateLocalRequest(request, QwenSetup.Definition.Implementation.Id) is { } invalid) return invalid;
        TensorBackendKind actual = QwenLlmInference.ResolveBackend(request.RequestedBackend);
        if (actual == TensorBackendKind.CoreMl
            || !OperatingSystem.IsMacOS() && actual is TensorBackendKind.Mlx or TensorBackendKind.GgmlMetal
            || OperatingSystem.IsMacOS() && actual is TensorBackendKind.Cuda or TensorBackendKind.GgmlCuda or TensorBackendKind.GgmlVulkan)
            return TensorBackendAvailability.Observe(request, TensorBackendAvailabilityState.Unsupported, "This backend is unsupported by Qwen on the target platform.");
        if (actual != TensorBackendKind.Cpu && TensorBackendProbes.RejectUnboundNativePlan(request) is { } unbound) return unbound;
        bool? available;
        try
        {
            available = actual switch
            {
                TensorBackendKind.Cpu => true,
                TensorBackendKind.Cuda => CudaBackend.IsAvailable(),
                TensorBackendKind.Mlx => CheckMlx(),
                TensorBackendKind.GgmlCpu => CheckGgml(GgmlBackendType.Cpu),
                TensorBackendKind.GgmlMetal => CheckGgml(GgmlBackendType.Metal),
                TensorBackendKind.GgmlCuda => CheckGgml(GgmlBackendType.Cuda),
                TensorBackendKind.GgmlVulkan => CheckGgml(GgmlBackendType.Vulkan),
                _ => false
            };
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or PlatformNotSupportedException or InvalidOperationException)
        { available = false; }
        catch (IOException) { available = null; }
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

    // Reports support without initializing the process-wide GGML backend, which would fix its type.
    private static bool CheckGgml(GgmlBackendType backend) => GgmlBasicOps.CanInitializeBackend(backend);

    // Reports whether the MLX native library is present without opening the GPU device.
    private static bool CheckMlx() => MlxBackend.IsAvailable();
}
