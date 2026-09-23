using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

/// <summary>Internal native boundary; hosts still select only typed pack keys and backends.</summary>
internal interface ILayaModelLoader
{
    Task<LayaModelResources> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend, CancellationToken cancellationToken);
}

internal interface ILayaDecisionModel : IDisposable
{
    LayaModelOutput Forward(LayaSequence sequence);
}

internal sealed class LayaModelResources(ILayaTokenizer tokenizer, ILayaDecisionModel model) : IDisposable
{
    public ILayaTokenizer Tokenizer { get; } = tokenizer;
    public ILayaDecisionModel Model { get; } = model;
    public void Dispose() => Model.Dispose();
}

internal sealed class LayaModelLoader : ILayaModelLoader
{
    public async Task<LayaModelResources> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend, CancellationToken cancellationToken)
    {
        LayaTokenizer tokenizer = await LayaTokenizer.CreateAsync(lease.GetRequiredArtifact("tokenizer"), cancellationToken).ConfigureAwait(false);
        using SafetensorsFile weights = await SafetensorsFile.OpenAsync(lease.GetRequiredArtifact("model"), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        LayaDecisionModel model = LayaDecisionModel.Load(weights, backend == TensorBackendKind.Cuda);
        return new(tokenizer, model);
    }
}
