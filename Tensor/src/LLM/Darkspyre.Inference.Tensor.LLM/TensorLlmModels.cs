using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.LLM;

public static class TensorLlmModels
{
    public static TensorModelPackDefinition Qwen3FourBillionQ4Km { get; } = new()
    {
        Key = new TensorModelKey("qwen/qwen3-4b-q4-k-m", "bc640142c66e1fdd12af0bd68f40445458f3869b"),
        Capability = TensorCapability.Llm,
        RuntimeArchitecture = "qwen3-gguf",
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
                Sources =
                [
                    new Uri(
                        "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/bc640142c66e1fdd12af0bd68f40445458f3869b/Qwen3-4B-Q4_K_M.gguf?download=true")
                ]
            }
        ]
    };
}
