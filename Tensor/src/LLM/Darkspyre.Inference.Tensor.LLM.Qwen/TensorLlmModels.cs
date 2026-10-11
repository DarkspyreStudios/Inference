using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM.Qwen;

/// <summary>Built-in LLM model pack definitions for the Qwen engine.</summary>
public static class TensorLlmModels
{
    /// <summary>The runtime architecture name the Qwen engine accepts for Qwen3 GGUF packs.</summary>
    public const string QwenRuntimeArchitecture = "qwen3-gguf";
    /// <summary>The pinned Qwen3 4B Q4_K_M GGUF pack from the Qwen/Qwen3-4B-GGUF repository.</summary>
    public static TensorModelPackDefinition Qwen3FourBillionQ4Km { get; } = new()
    {
        Key = new TensorModelKey("qwen/qwen3-4b-q4-k-m", "bc640142c66e1fdd12af0bd68f40445458f3869b"),
        Capability = TensorCapability.Llm,
        RuntimeArchitecture = QwenRuntimeArchitecture,
        License = "Apache-2.0",
        ProjectUri = new Uri("https://huggingface.co/Qwen/Qwen3-4B-GGUF"),
        SupportedBackends = new HashSet<TensorBackendKind>
        {
            TensorBackendKind.Cpu,
            TensorBackendKind.GgmlCpu,
            TensorBackendKind.GgmlMetal,
            TensorBackendKind.GgmlCuda,
            TensorBackendKind.GgmlVulkan,
            TensorBackendKind.Cuda,
            TensorBackendKind.Mlx
        },
        Artifacts =
        [
            new TensorModelArtifactDefinition
            {
                Role = "model",
                RelativePath = "Qwen3-4B-Q4_K_M.gguf",
                Length = 2_497_280_256,
                Sha256 = "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5",
                Provenance = new(new("https://huggingface.co/"), "Qwen/Qwen3-4B-GGUF",
                    "bc640142c66e1fdd12af0bd68f40445458f3869b", "Qwen3-4B-Q4_K_M.gguf", "Apache-2.0"),
                Sources =
                [
                    new Uri(
                        "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/bc640142c66e1fdd12af0bd68f40445458f3869b/Qwen3-4B-Q4_K_M.gguf?download=true")
                ]
            }
        ]
    };
}
