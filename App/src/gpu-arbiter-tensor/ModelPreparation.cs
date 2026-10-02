using System.Text.Json;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.LLM.Qwen;

namespace GpuArbiter.Tensor;

internal static class ModelPreparation
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] is not ("--file" or "--store" or "--manifest" or "--license" or "--backend") ||
                !arguments.TryAdd(args[index], args[index + 1])) throw new ArgumentException("Invalid model preparation arguments.");
        }
        string Required(string name) => arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"{name} is required.");
        var file = Path.GetFullPath(Required("--file"));
        var store = Path.GetFullPath(Required("--store"));
        var manifest = Path.GetFullPath(Required("--manifest"));
        if (File.Exists(manifest)) throw new ArgumentException("The manifest already exists. Use a new path for a new immutable pack.");
        var backend = arguments.TryGetValue("--backend", out var selected) ? Enum.Parse<TensorBackendKind>(selected, true) : TensorBackendKind.Auto;
        using var packs = new TensorModelPackManager(new TensorModelCatalog([]), Path.Combine(store, "Qwen"));
        var result = await packs.ImportAsync(new(new(new TensorModelLocation.LocalFile(file), backend, Required("--license")), new QwenModelResolver()), cancellationToken: token);
        if (result.Resolution.Evaluation.State != TensorCompatibilityState.Supported || result.Resolution.Definition is not { } definition)
            throw new InvalidOperationException("The local checkpoint is not supported by the Qwen adapter.");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        await using var output = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(output, definition, TensorModelHost.ManifestJson, token);
        Console.WriteLine($"Prepared {definition.Key}. Configure its manifest and darkspyre.tensor.qwen in Tensor:Models.");
        return 0;
    }
}
