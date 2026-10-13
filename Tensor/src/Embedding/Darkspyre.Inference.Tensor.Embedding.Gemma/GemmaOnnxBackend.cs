using Darkspyre.Inference.Common;
using Darkspyre.Inference.Tensor.Common;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;

namespace Darkspyre.Inference.Tensor.Embedding.Gemma;

internal static class GemmaOnnxBackend
{
    // OrtEnv owns provider registration for the process. Model instances own only their sessions.
    private static readonly Lazy<OrtEnv> WebGpu = new(() =>
    {
        OrtEnv environment = OrtEnv.Instance();
        environment.RegisterExecutionProviderLibrary("darkspyre_embedding_gemma_webgpu", WebGpuEp.GetLibraryPath());
        return environment;
    });

    internal static TensorBackendKind Resolve(TensorBackendKind requested) => requested == TensorBackendKind.Auto
        ? TensorBackendKind.WebGpu : requested;

    internal static SessionOptions CreateOptions(TensorBackendKind backend)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        try
        {
            if (backend == TensorBackendKind.WebGpu)
            {
                OrtEnv environment = WebGpu.Value;
                OrtEpDevice device = environment.GetEpDevices().FirstOrDefault(device => device.EpName == WebGpuEp.GetEpName()
                    && device.EpMetadata.Entries.ContainsKey("library_path") && device.HardwareDevice.Type == OrtHardwareDeviceType.GPU)
                    ?? throw new InferenceException(InferenceFailureCode.BackendUnavailable, "The packaged WebGPU provider found no GPU.");
                options.AppendExecutionProvider(environment, [device], new Dictionary<string, string>());
            }
            else if (backend != TensorBackendKind.Cpu)
                throw new InferenceException(InferenceFailureCode.BackendUnavailable, "EmbeddingGemma supports CPU and WebGPU.");
            return options;
        }
        catch { options.Dispose(); throw; }
    }
}
