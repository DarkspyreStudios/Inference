using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Image.Supra;

internal interface ISupraImageModel : IDisposable
{
    byte[] Generate(string prompt, long seed, int steps, float guidanceScale, Action<int, int>? stepCompleted,
        Action? decodingStarted, CancellationToken cancellationToken);
}

internal interface ISupraModelLoader
{
    Task<ISupraImageModel> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend, CancellationToken cancellationToken);
}

internal sealed class SupraModelLoader : ISupraModelLoader
{
    /// <summary>
    /// Reads the tokenizer and weight files and builds the model on a background thread. TensorSharp
    /// and ML.Tokenizers read these files synchronously.
    /// </summary>
    public Task<ISupraImageModel> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend, CancellationToken cancellationToken) =>
        Task.Run<ISupraImageModel>(() =>
        {
            SupraTextTokenizer tokenizer = SupraTextTokenizer.Create(lease.GetRequiredArtifact("text-tokenizer"));
            using var textWeights = new SafetensorsFile(lease.GetRequiredArtifact("text-model"));
            using var ditWeights = new TorchStateDictionaryFile(lease.GetRequiredArtifact("supra-model"));
            using var vaeWeights = new SafetensorsFile(lease.GetRequiredArtifact("vae-model"));
            cancellationToken.ThrowIfCancellationRequested();
            SupraCheckpointSchema.ValidateDit(ditWeights.Tensors, ditWeights.IntegerMetadata);
            SupraCheckpointSchema.ValidateText(textWeights.Tensors);
            SupraCheckpointSchema.ValidateVae(vaeWeights.Tensors);
            return new SupraImageModel(tokenizer, textWeights, ditWeights, vaeWeights, backend == TensorBackendKind.Cuda);
        }, cancellationToken);
}

internal static class SupraModelCompatibility
{
    public static void Validate(TensorModelPackDefinition definition)
    {
        TensorModelDefinitionValidator.Validate(definition);
        if (definition.Capability != TensorCapability.Image || definition.RuntimeArchitecture != TensorImageModels.SupraRuntimeArchitecture)
        { throw new ArgumentException("The pack is not a Supra2 image pipeline.", nameof(definition)); }
        string[] roles = ["supra-model", "text-model", "text-tokenizer", "vae-model"];
        if (definition.Artifacts.Count != roles.Length || roles.Any(role => !definition.Artifacts.Any(artifact => artifact.Role == role)))
        { throw new ArgumentException("A Supra pack requires exactly the DiT, text encoder, tokenizer and VAE artifacts.", nameof(definition)); }
        if (definition.SupportedBackends.Count == 0 || definition.SupportedBackends.Any(backend => backend is not (TensorBackendKind.Cpu or TensorBackendKind.Cuda)))
        { throw new ArgumentException("Supra pack backends must be CPU and/or CUDA.", nameof(definition)); }
    }
}
