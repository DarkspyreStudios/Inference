using System.Collections.Frozen;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

/// <summary>The pack architecture and backends of the Qwen engine. Qwen packs arrive by import.</summary>
public static class TensorLlmModels
{
    /// <summary>The runtime architecture name of Qwen GGUF packs. The value predates Qwen 3.5 and stays, because
    /// installed packs record it.</summary>
    public const string QwenRuntimeArchitecture = "qwen3-gguf";

    /// <summary>The pack role of a Qwen 3.8 Flash Next vision projector GGUF (a CLIP mmproj file).</summary>
    public const string VisionProjectorRole = "vision-projector";

    /// <summary>The pack role of a Qwen 3.8 Flash Next MTP draft GGUF (one shared-target NextN layer).</summary>
    public const string MtpDraftRole = "mtp-draft";

    /// <summary>The reason an installed Qwen3 GGUF is not supported.</summary>
    public const string Qwen3RetiredReason = "Qwen3 models are no longer supported. Qwen 3.5, Qwen 3.5 MoE, Qwen3-Next and Qwen 3.8 Flash Next models are.";

    /// <summary>The backends the Qwen engine runs a pack on.</summary>
    public static IReadOnlySet<TensorBackendKind> Backends { get; } = new[]
    {
        TensorBackendKind.Cpu, TensorBackendKind.GgmlCpu, TensorBackendKind.GgmlMetal, TensorBackendKind.GgmlCuda,
        TensorBackendKind.GgmlVulkan, TensorBackendKind.Cuda, TensorBackendKind.Mlx
    }.ToFrozenSet();
}
