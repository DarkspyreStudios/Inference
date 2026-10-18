using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Models.Embeddings;

namespace Darkspyre.Inference.Tensor.Embedding;

internal interface IEmbeddingModelLoader
{
    Task<IEmbeddingModel> LoadAsync(TensorModelPackDefinition definition, TensorEmbeddingArchitecture architecture,
        TensorModelPackLease lease, TensorBackendKind backend, CancellationToken token);
}

/// <summary>Loads a GGUF embedding model on managed CPU or a native GGML backend the caller has initialised.</summary>
internal sealed class EmbeddingModelLoader : IEmbeddingModelLoader
{
    public async Task<IEmbeddingModel> LoadAsync(TensorModelPackDefinition definition, TensorEmbeddingArchitecture architecture,
        TensorModelPackLease lease, TensorBackendKind backend, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = lease.GetRequiredArtifact("model");
        if (!File.Exists(path) || new FileInfo(path).Length != definition.Artifacts[0].Length)
            throw new InvalidDataException("The installed embedding artifact is missing or changed.");
        // The engine receives the actual owner before checking cancellation or model metadata.
        return await Task.Run<IEmbeddingModel>(() => EmbeddingModel.Load(path, new EmbeddingModelOptions
        {
            Backend = BackendName(backend),
            MaxTokens = architecture.MaximumInputTokens,
            ModelName = definition.Key.ModelId,
        }), CancellationToken.None).ConfigureAwait(false);
    }

    private static string BackendName(TensorBackendKind backend) => backend switch
    {
        TensorBackendKind.Cpu => "CPU",
        TensorBackendKind.GgmlCpu => "GGML_CPU",
        TensorBackendKind.GgmlMetal => "GGML_METAL",
        _ => throw new ArgumentOutOfRangeException(nameof(backend), "TensorSharp's embedding encoders do not run on this backend."),
    };
}
