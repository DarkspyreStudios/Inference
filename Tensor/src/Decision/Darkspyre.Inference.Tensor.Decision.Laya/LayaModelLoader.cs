using Darkspyre.Inference.Tensor.Common;
using Microsoft.ML.OnnxRuntime;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

/// <summary>Internal native boundary; hosts still select only typed pack keys and backends.</summary>
internal interface ILayaModelLoader
{
    Task<LayaModelResources> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend,
        LayaAgentConfiguration configuration, CancellationToken cancellationToken);
}

internal interface ILayaDecisionModel : IDisposable
{
    /// <summary>The largest option count one sequence can carry.</summary>
    int MaximumOptions { get; }

    LayaModelOutput Forward(LayaSequence sequence, CancellationToken cancellationToken);
}

internal sealed class LayaModelResources(ILayaTokenizer tokenizer, ILayaDecisionModel model) : IDisposable
{
    public ILayaTokenizer Tokenizer { get; } = tokenizer;
    public ILayaDecisionModel Model { get; } = model;
    public void Dispose() => Model.Dispose();
}

internal sealed class LayaModelLoader : ILayaModelLoader
{
    public async Task<LayaModelResources> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend,
        LayaAgentConfiguration configuration, CancellationToken cancellationToken)
    {
        LayaTokenizer tokenizer = await LayaTokenizer.CreateAsync(lease.GetRequiredArtifact("tokenizer"), cancellationToken).ConfigureAwait(false);
        byte[] model = await ReadAsync(lease.GetRequiredArtifact("model"), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using SessionOptions options = CreateOptions(backend);
        return new(tokenizer, LayaOnnxModel.Create(model, options, configuration.MaximumLength));
    }

    /// <summary>
    /// Selects execution providers for a resolved backend. A later CUDA phase adds its case here; until
    /// then compatibility validation rejects CUDA packs before this runs.
    /// </summary>
    internal static SessionOptions CreateOptions(TensorBackendKind backend)
    {
        var options = new SessionOptions();
        try
        {
            switch (backend)
            {
                case TensorBackendKind.CoreMl:
                    // CPUAndGPU excludes the Neural Engine. It decides as fast as ALL on the Laya graph
                    // and skips the Neural Engine preparation that lengthens every load.
                    options.AppendExecutionProvider("CoreML", new Dictionary<string, string>
                    {
                        ["ModelFormat"] = "MLProgram",
                        ["MLComputeUnits"] = "CPUAndGPU",
                        ["RequireStaticInputShapes"] = "1"
                    });
                    break;
                case TensorBackendKind.Cpu:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(backend), backend, "Laya runs only on the Core ML and CPU backends.");
            }

            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReadAsync(PersistenceFileReference source, CancellationToken cancellationToken)
    {
        await using Stream content = await source.Store.OpenReadAsync(
            source.StoreName,
            source.AssetName,
            cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Model artifact '{source}' does not exist.", source.AssetName);
        if (content.CanSeek)
        {
            byte[] buffer = new byte[checked((int)(content.Length - content.Position))];
            await content.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer;
        }

        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }
}
