using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Models.Embeddings;

namespace Darkspyre.Inference.Tensor.Embedding;

internal interface IEmbeddingModelLoader
{
    Task<IEmbeddingModel> LoadAsync(TensorModelPackDefinition definition, TensorEmbeddingArchitecture architecture,
        TensorModelPackLease lease, CancellationToken token);
}

/// <summary>Loads a GGUF sentence encoder on the managed CPU backend.</summary>
internal sealed class EmbeddingModelLoader : IEmbeddingModelLoader
{
    public async Task<IEmbeddingModel> LoadAsync(TensorModelPackDefinition definition, TensorEmbeddingArchitecture architecture,
        TensorModelPackLease lease, CancellationToken token)
    {
        string path = lease.GetRequiredArtifact("model");
        if (!File.Exists(path) || new FileInfo(path).Length != definition.Artifacts[0].Length)
            throw new InvalidDataException("The installed embedding artifact is missing or changed.");
        // Do not abandon a load on cancellation: wait for it, then dispose the loaded model.
        IEmbeddingModel model = await Task.Run(() => (IEmbeddingModel)EmbeddingModel.Load(path, new EmbeddingModelOptions
        {
            Backend = "CPU", MaxTokens = architecture.MaximumInputTokens, ModelName = definition.Key.ModelId,
        }), CancellationToken.None).ConfigureAwait(false);
        if (token.IsCancellationRequested || model.Dimensions != architecture.NativeDimensions)
        {
            model.Dispose();
            token.ThrowIfCancellationRequested();
            throw new InvalidDataException("The embedding model's vector size does not match its architecture.");
        }
        return model;
    }
}
