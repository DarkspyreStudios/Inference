using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Embedding.Gemma;

/// <summary>The verified FP32 EmbeddingGemma 2 text and image pack.</summary>
public static class GemmaEmbeddingModels
{
    /// <summary>The ONNX publisher's repository.</summary>
    public const string Repository = "onnx-community/embeddinggemma-2-ONNX";
    /// <summary>The immutable revision of the graphs, external weights and tokenizer.</summary>
    public const string Revision = "daa72c51243991dfcaf9f9137d2c573d8f7790c0";
    internal const string Architecture = "embeddinggemma2-onnx";
    internal const int MaximumTokens = 8192;
    internal const int Dimensions = 768;
    internal static readonly IReadOnlySet<TensorBackendKind> Backends = new[] { TensorBackendKind.Cpu, TensorBackendKind.WebGpu }.ToFrozenSet();
    internal static readonly IReadOnlySet<EmbeddingModality> Modalities = new[] { EmbeddingModality.Text, EmbeddingModality.Image }.ToFrozenSet();

    /// <summary>FP32 text/image graphs with the pinned tokenizer. Installation is explicit.</summary>
    public static TensorModelPackDefinition EmbeddingGemma2 { get; } = new()
    {
        Key = new("google/embeddinggemma-2-onnx-fp32", Revision),
        Capability = TensorCapability.Embedding,
        RuntimeArchitecture = Architecture,
        License = "Apache-2.0",
        ProjectUri = new("https://huggingface.co/" + Repository),
        SupportedBackends = Backends,
        Artifacts = Array.AsReadOnly<TensorModelArtifactDefinition>([
            Artifact("model", "model.onnx", "onnx/model.onnx", 421421, "bc47de15f81208a5c99e5ab10f746d5e33b51ea228b7dc0bef9c133a94f1c1c3"),
            Artifact("model-data", "model.onnx_data", "onnx/model.onnx_data", 1084170240, "9fd452bfc6916e5a92f080f8dada35598ca4512b21c65897d4c5974d5f9630a2"),
            Artifact("vision", "vision_encoder.onnx", "onnx/vision_encoder.onnx", 121253, "a3fb44f8b5b152cfee6afe16e2cd69e774b7e1d3b822d1db1190ddf7b08b575e"),
            Artifact("vision-data", "vision_encoder.onnx_data", "onnx/vision_encoder.onnx_data", 671026176, "ca8d28c99feb8129618f141978a27adae25090e0dadb68a30c235e191c01e37e"),
            Artifact("tokenizer", "tokenizer.json", "tokenizer.json", 32170510, "4d777ef5bdc1aa36227abdfb77c3e49e7b9c892d16e1b6bda41c393504828be4")])
    };

    private static TensorModelArtifactDefinition Artifact(string role, string path, string source, long length, string hash) => new()
    {
        Role = role,
        RelativePath = path,
        Length = length,
        Sha256 = hash,
        Provenance = new(new("https://huggingface.co/"), Repository, Revision, source, "Apache-2.0"),
        Sources = Array.AsReadOnly<Uri>([new($"https://huggingface.co/{Repository}/resolve/{Revision}/{source}")])
    };

    internal static void Validate(TensorModelPackDefinition definition)
    {
        TensorModelDefinitionValidator.Validate(definition);
        if (definition.Capability != TensorCapability.Embedding || definition.RuntimeArchitecture != Architecture
            || definition.SupportedBackends.Count == 0 || !definition.SupportedBackends.IsSubsetOf(Backends)
            || definition.Artifacts.Count != EmbeddingGemma2.Artifacts.Count
            || EmbeddingGemma2.Artifacts.Any(required => !definition.Artifacts.Any(artifact =>
                artifact.Role == required.Role && artifact.RelativePath == required.RelativePath)))
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "This is not a compatible EmbeddingGemma ONNX pack.");
    }

    internal static string Fingerprint(TensorModelPackDefinition definition)
    {
        string identity = string.Join('\n', "embeddinggemma2-onnx-fp32-1", "ort-1.30.0-webgpu-0.4.0",
            "task: search result | query: {text}", "title: {title-or-none} | text: {text}",
            "gemma4-pil-rgb-22bit-patch16-pool3-budget280", "sentence_embedding-768-l2",
            string.Join('\n', definition.Artifacts.OrderBy(artifact => artifact.Role, StringComparer.Ordinal)
                .Select(artifact => artifact.Role + "=" + artifact.Sha256.ToLowerInvariant())));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
