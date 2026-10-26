using System.Reflection;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Models;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

/// <summary>Reads where TensorSharp placed a loaded qwen4exp model's routed experts.</summary>
internal static class QwenExpertPlacement
{
    /// <summary>Returns the GPU layer count, or null for CPU, another architecture or a missing upstream field.</summary>
    public static TensorExpertPlacement? From(ModelBase? model, string? backend)
    {
        if (model is Qwen4ExpModel && backend is "ggml_metal" or "ggml_cuda" or "ggml_vulkan")
        {
            // TensorSharp 2026.10.11 exposes its plan only here. QwenExpertPlacementTests checks the pinned field.
            return typeof(Qwen4ExpModel).GetField("_expertOnHost", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(model) is bool[] onHost
                ? new(onHost.Count(host => !host), onHost.Length) : null;
        }
        return model is Qwen4ExpCudaModel cuda && backend == "cuda"
            ? new(cuda.Config.NumLayers, cuda.Config.NumLayers) : null;
    }
}
