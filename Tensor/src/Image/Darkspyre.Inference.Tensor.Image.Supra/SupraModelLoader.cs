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
    public async Task<ISupraImageModel> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend, CancellationToken cancellationToken)
    {
        SupraTextTokenizer tokenizer = await SupraTextTokenizer.CreateAsync(lease.GetRequiredArtifact("text-tokenizer"), cancellationToken).ConfigureAwait(false);
        using SafetensorsFile textWeights = await SafetensorsFile.OpenAsync(lease.GetRequiredArtifact("text-model"), cancellationToken).ConfigureAwait(false);
        using TorchStateDictionaryFile ditWeights = await TorchStateDictionaryFile.OpenAsync(lease.GetRequiredArtifact("supra-model"), cancellationToken).ConfigureAwait(false);
        using SafetensorsFile vaeWeights = await SafetensorsFile.OpenAsync(lease.GetRequiredArtifact("vae-model"), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // Constructors validate the actual tensor schema before the resident model is published.
        return new SupraImageModel(tokenizer, textWeights, ditWeights, vaeWeights, backend == TensorBackendKind.Cuda);
    }
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
