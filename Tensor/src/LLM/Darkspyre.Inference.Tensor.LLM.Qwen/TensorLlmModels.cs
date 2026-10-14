using System.Collections.Frozen;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

/// <summary>The pack architecture and backends of the Qwen engine. Qwen packs arrive by import.</summary>
public static class TensorLlmModels
{
    /// <summary>The runtime architecture name the Qwen engine accepts for Qwen3 GGUF packs.</summary>
    public const string QwenRuntimeArchitecture = "qwen3-gguf";

    /// <summary>The backends the Qwen engine runs a pack on.</summary>
    public static IReadOnlySet<TensorBackendKind> Backends { get; } = new[]
    {
        TensorBackendKind.Cpu, TensorBackendKind.GgmlCpu, TensorBackendKind.GgmlMetal, TensorBackendKind.GgmlCuda,
        TensorBackendKind.GgmlVulkan, TensorBackendKind.Cuda, TensorBackendKind.Mlx
    }.ToFrozenSet();
}
