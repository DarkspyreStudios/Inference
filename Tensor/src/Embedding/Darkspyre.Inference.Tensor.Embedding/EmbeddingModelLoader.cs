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
        string path = lease.GetRequiredArtifact("model");
        if (!File.Exists(path) || new FileInfo(path).Length != definition.Artifacts[0].Length)
            throw new InvalidDataException("The installed embedding artifact is missing or changed.");
        // Do not abandon a load on cancellation: wait for it, then dispose the loaded model.
        IEmbeddingModel model = await Task.Run(() => EmbeddingModelFactory.Load(path, new EmbeddingModelOptions
        {
            Backend = BackendName(backend), MaxTokens = architecture.MaximumInputTokens, ModelName = definition.Key.ModelId,
        }), CancellationToken.None).ConfigureAwait(false);
        if (token.IsCancellationRequested || model.Architecture != architecture.GgufArchitecture
            || model.Dimensions != architecture.NativeDimensions)
        {
            model.Dispose();
            token.ThrowIfCancellationRequested();
            throw new InvalidDataException("The embedding model's architecture or vector size does not match its pack.");
        }
        return model;
    }

    private static string BackendName(TensorBackendKind backend) => backend switch
    {
        TensorBackendKind.Cpu => "CPU",
        TensorBackendKind.GgmlCpu => "GGML_CPU",
        TensorBackendKind.GgmlMetal => "GGML_METAL",
        _ => throw new ArgumentOutOfRangeException(nameof(backend), "TensorSharp's embedding encoders do not run on this backend."),
    };
}
