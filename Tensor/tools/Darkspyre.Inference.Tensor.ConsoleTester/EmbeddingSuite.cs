using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Embedding;
using Darkspyre.Inference.Tensor.Ggml;
using Darkspyre.Persist;

namespace Darkspyre.Inference.Tensor.ConsoleTester;

internal static class EmbeddingSuite
{
    internal static ConsoleOptions Parse(string[] args)
    {
        var options = new ConsoleOptions();
        for (int index = 1; index < args.Length; index++)
        {
            string name = args[index];
            string Value() => ++index < args.Length ? args[index] : throw new ArgumentException($"{name} needs a value.");
            options = name switch
            {
                "--store" => options with { Store = Path.GetFullPath(Value()) },
                "--backend" => options with { Backend = Enum.Parse<TensorBackendKind>(Value(), ignoreCase: true) },
                _ => throw new ArgumentException($"Unknown embedding option '{name}'.")
            };
        }
        if (options.Store is null) throw new ArgumentException("--store is required: name the model store.");
        return options;
    }

    internal static async Task<int> RunAsync(ConsoleOptions options, CancellationToken token)
    {
        GgmlRuntimeOwner runtime = GgmlRuntimeOwner.GetOrCreate();
        var provider = new EmbeddingEngineProvider(runtime);
        IReadOnlyList<TensorModelPackDefinition> installed = await provider.GetInstalledModelsAsync(new OsUserPersist(options.Store!), token);
        TensorModelPackDefinition definition = installed.Count > 0 ? installed[0]
            : throw new InvalidOperationException("No Embedding model is installed in this store.");
        using var packs = new TensorModelPackManager(new TensorModelCatalog([definition]),
            Path.Combine(options.Store!, provider.Definition.ModelStoreKey));
        var engine = new TensorEmbeddingInference(packs, options.Backend, runtime);
        return await Program.RunOwnedEngineAsync(async () =>
        {
            await Program.AwaitStateAsync(engine, () => engine.StartAsync(token), TensorEngineState.Started);
            await Program.AwaitStateAsync(engine, () => engine.LoadAsync(definition.Key, token), TensorEngineState.Loaded);
            TensorModelObservation observation = await engine.GetObservationAsync(token);
            EmbeddingCapabilities capabilities = await engine.GetCapabilitiesAsync(definition.Key.ModelId, token);
            Console.WriteLine($"loaded  : {definition.Key}; {observation.Backend}; {capabilities.NativeDimensions} dimensions");
            if (options.Backend is not (TensorBackendKind.Cpu or TensorBackendKind.Auto) && observation.Backend != options.Backend)
                throw new InvalidOperationException("The engine did not use the requested backend.");
            EmbeddingResult documents = await engine.EmbedAsync(new()
            {
                ModelId = definition.Key.ModelId,
                Inputs = [new TextEmbeddingInput("Dispose the loaded model before shutting down the native runtime."),
                    new TextEmbeddingInput("The invoice lists the purchased items and their prices.")],
                Dimensions = 512
            }, token);
            EmbeddingResult query = await engine.EmbedAsync(new()
            {
                ModelId = definition.Key.ModelId,
                Inputs = [new TextEmbeddingInput("How do I release the native model and runtime?")],
                Kind = EmbeddingInputKind.Query,
                Dimensions = 512
            }, token);
            Verify(documents, definition, capabilities, 2, observation.Backend);
            Verify(query, definition, capabilities, 1, observation.Backend);
            double relevant = Dot(query.Vectors[0], documents.Vectors[0]);
            double unrelated = Dot(query.Vectors[0], documents.Vectors[1]);
            if (relevant <= unrelated) throw new InvalidOperationException("The query did not rank the matching document first.");
            Console.WriteLine($"PASS: document/query embeddings; cosine {relevant:F4} > {unrelated:F4}; input tokens {documents.Usage.InputTokens}/{query.Usage.InputTokens}.");
            if (options.Backend != TensorBackendKind.Cpu)
            {
                TensorRuntimeShutdownResult busy = await runtime.ShutdownAsync(token);
                Console.WriteLine("busy    : " + JsonSerializer.Serialize(busy, TensorJson.TensorRuntimeShutdownResult));
                if (busy.State != TensorRuntimeShutdownState.Busy || busy.OutstandingLeases <= 0)
                    throw new InvalidOperationException("Runtime shutdown did not refuse the loaded embedding model.");
            }
            return 0;
        }, engine, runtime, Console.Out);
    }

    private static void Verify(EmbeddingResult result, TensorModelPackDefinition definition,
        EmbeddingCapabilities capabilities, int count, TensorBackendKind? backend)
    {
        EmbeddingResult restored = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(result, InferenceJson.EmbeddingResult), InferenceJson.EmbeddingResult)
            ?? throw new InvalidOperationException("The embedding result JSON returned null.");
        if (restored.Dimensions != 512 || restored.Vectors.Count != count || restored.TruncatedInputs.Count != 0
            || restored.ModelFingerprint != capabilities.ModelFingerprint || restored.Usage != result.Usage
            || restored.Usage.InputTokens <= 0 || restored.Provenance.ModelId != definition.Key.ModelId
            || restored.Provenance.ModelRevision != definition.Key.Revision || restored.Provenance.Backend != backend?.ToString())
            throw new InvalidOperationException("The result lost its embedding shape or actual identity.");
        for (int index = 0; index < count; index++)
        {
            float[] vector = restored.Vectors[index];
            if (vector.Length != 512 || !vector.SequenceEqual(result.Vectors[index]) || vector.Any(value => !float.IsFinite(value))
                || Math.Abs(Dot(vector, vector) - 1) > 0.001)
                throw new InvalidOperationException("The result lost its ordered unit-length vector values.");
        }
    }

    private static double Dot(float[] left, float[] right) => left.Zip(right, (a, b) => (double)a * b).Sum();
}
