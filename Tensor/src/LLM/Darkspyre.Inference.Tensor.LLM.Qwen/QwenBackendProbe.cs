using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Cuda;
using TensorSharp.GGML;
using TensorSharp.MLX;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

internal sealed class QwenBackendProbe : ITensorBackendProbe
{
    // Loading a native library or runtime to answer is synchronous work.
    public Task<TensorBackendAvailability> CheckAsync(TensorBackendKind backend, CancellationToken cancellationToken = default) =>
        Task.Run(() => Check(backend, cancellationToken), cancellationToken);

    private static TensorBackendAvailability Check(TensorBackendKind backend, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TensorBackendKind actual = QwenLlmInference.ResolveBackend(backend);
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
        return new TensorBackendAvailability(backend, available,
            available == true ? $"{actual} runtime prerequisites are available. Model allocation is not guaranteed."
                : available == false ? $"{actual} runtime/device prerequisites are unavailable." : $"{actual} availability is unknown.", DateTimeOffset.UtcNow);
    }

    // Reports support without initializing the process-wide GGML backend, which would fix its type.
    private static bool CheckGgml(GgmlBackendType backend) => GgmlBasicOps.CanInitializeBackend(backend);

    // Reports whether the MLX native library is present without opening the GPU device.
    private static bool CheckMlx() => MlxBackend.IsAvailable();
}
