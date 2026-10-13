using System.Diagnostics;
using System.Text.Json;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Embedding.Gemma;
using Microsoft.Extensions.DependencyInjection;

namespace Darkspyre.Inference.Tensor.ConsoleTester;

/// <summary>Exercises the installed Gemma library and its Connect consumer path with reference fixtures.</summary>
internal static class GemmaSuite
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 5 || args[1] is not ("cpu" or "webgpu"))
            throw new ArgumentException("Usage: gemma <cpu|webgpu> <model-directory> <fixture-directory> <output-directory>");
        string modelDirectory = Path.GetFullPath(args[2]), fixtures = Path.GetFullPath(args[3]), output = Path.GetFullPath(args[4]);
        Directory.CreateDirectory(output);
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "engine.json")));
        GemmaTokenizer tokenizer = GemmaTokenizer.Load(Path.Combine(modelDirectory, "tokenizer.json"));
        foreach (var item in fixture.RootElement.GetProperty("tokenizer").EnumerateArray())
        {
            string text = item.GetProperty("text").GetString()!;
            if (!tokenizer.Encode(text).SequenceEqual(item.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32())))
                throw new InvalidDataException("Tokenizer parity failed for: " + JsonSerializer.Serialize(text));
        }
        Console.WriteLine("Tokenizer parity passed.");
        TensorModelPackDefinition definition = GemmaEmbeddingModels.EmbeddingGemma2;
        definition = definition with
        {
            Artifacts = definition.Artifacts.Select(artifact => artifact with
            { Sources = new[] { new Uri(Path.Combine(modelDirectory, artifact.RelativePath)) } }).ToArray()
        };
        using var packs = new TensorModelPackManager(new TensorModelCatalog([definition]), Path.Combine(output, "store"));
        await packs.InstallAsync(definition.Key);
        TensorBackendKind backend = args[1] == "cpu" ? TensorBackendKind.Cpu : TensorBackendKind.WebGpu;
        await using var engine = new GemmaEmbeddingInference(packs, backend);
        var capabilities = await engine.GetCapabilitiesAsync();
        if (!capabilities.Modalities.SetEquals([EmbeddingModality.Text, EmbeddingModality.Image]))
            throw new InvalidDataException("Gemma capabilities omit an input modality.");
        var watch = Stopwatch.StartNew();
        await Program.AwaitStateAsync(engine, () => engine.LoadAsync(definition.Key), TensorEngineState.Loaded);
        double loadMs = watch.Elapsed.TotalMilliseconds;
        Console.WriteLine($"Loaded {backend} in {loadMs:F1} ms.");
        var services = new ServiceCollection();
        services.AddLlmConnect();

        services.AddSingleton<ILlmSelfHost>(new Engines(engine));
        services.AddSingleton<ILlmConnectionSource>(new Connections());
        await using ServiceProvider provider = services.BuildServiceProvider();
        LlmEmbeddingProviderService connect = provider.GetRequiredService<LlmEmbeddingProviderService>();
        var results = new List<object>();
        var vectors = new Dictionary<string, float[]>();
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            EmbeddingInput input = item.TryGetProperty("image", out var path)
                ? new MediaEmbeddingInput(new("image/png", File.ReadAllBytes(Path.Combine(fixtures, path.GetString()!))))
                : new TextEmbeddingInput(item.GetProperty("text").GetString()!, item.TryGetProperty("title", out var title) ? title.GetString() : null);
            var request = new EmbeddingRequest
            {
                ModelId = definition.Key.ModelId, Inputs = [input],
                Kind = item.GetProperty("kind").GetString() == "query" ? EmbeddingInputKind.Query : EmbeddingInputKind.Document
            };
            watch.Restart();
            EmbeddingResult direct = await engine.EmbedAsync(request);
            double directMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            LlmEmbeddingResult throughConnect = await connect.EmbedAsync("gemma", new()
            { ModelId = request.ModelId, Inputs = request.Inputs, Kind = request.Kind });
            double connectMs = watch.Elapsed.TotalMilliseconds;
            float[] vector = direct.Vectors.Single();
            double maxError = vector.Zip(throughConnect.Vectors.Single(), (a, b) => Math.Abs((double)a - b)).Max();
            if (maxError > 1e-5 || direct.ModelFingerprint != throughConnect.ModelFingerprint)
                throw new InvalidDataException("Connect embedding parity failed.");
            vectors.Add(name, vector);
            results.Add(new { name, directMs, connectMs, maxError, vector, direct.Usage, direct.Provenance, direct.ModelFingerprint });
            Console.WriteLine($"{name}: direct {directMs:F1} ms; Connect {connectMs:F1} ms; max error {maxError:G3}");
        }
        foreach (var check in fixture.RootElement.GetProperty("retrieval").EnumerateArray())
        {
            string query = check.GetProperty("query").GetString()!, expected = check.GetProperty("expected").GetString()!;
            string winner = check.GetProperty("documents").EnumerateArray().Select(value => value.GetString()!)
                .OrderByDescending(name => vectors[name].Zip(vectors[query], (a, b) => (double)a * b).Sum()).First();
            if (winner != expected) throw new InvalidDataException($"Recall fixture {query} matched {winner}, expected {expected}.");
        }
        // A rejected batch and a cancelled call must leave the loaded engine usable.
        try
        {
            await engine.EmbedAsync(new() { ModelId = definition.Key.ModelId,
                Inputs = [new TextEmbeddingInput("valid"), new TextEmbeddingInput(string.Concat(Enumerable.Repeat("word ", 9000)))] });
            throw new InvalidDataException("The over-limit request was accepted.");
        }
        catch (EmbeddingInputTooLongException error) when (error.Inputs.Count == 1 && error.Inputs[0].Index == 1) { }
        using (var cancellation = new CancellationTokenSource())
        {
            Task<EmbeddingResult> pending = engine.EmbedAsync(new() { ModelId = definition.Key.ModelId,
                Inputs = [new TextEmbeddingInput(string.Concat(Enumerable.Repeat("word ", 1024)))] }, cancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
            try { await pending; throw new InvalidDataException("The request completed before cancellation could be observed."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        await engine.EmbedAsync(new() { ModelId = definition.Key.ModelId, Inputs = [new TextEmbeddingInput("usable after cancellation")] });
        await Program.AwaitStateAsync(engine, () => engine.UnloadAsync(), TensorEngineState.Unloaded);
        await packs.RemoveAsync(definition.Key);
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        { backend = backend.ToString(), loadMs, tokenizerParity = true, retrievalPassed = true, cancellationPassed = true, releasedPack = true, results }));
        Console.WriteLine("Recall, over-limit rejection, cancellation/reuse and unload passed.");
        return 0;
    }

    private sealed class Engines(IEmbeddingInference engine) : ILlmSelfHost
    {
        public Task<IReadOnlyList<LlmSelfHostEngine>> ListEnginesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LlmSelfHostEngine>>([new("gemma", "Gemma")]);
        public Task<IEmbeddingInference> GetEmbeddingInferenceAsync(string engineId, CancellationToken cancellationToken = default) => Task.FromResult(engine);
        public Task<ILlmInference> GetInferenceAsync(string engineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public LlmCapabilities? GetCapabilities(string engineId, string? modelId) => null;
    }

    private sealed class Connections : ILlmConnectionSource
    {
        public Task<LlmConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken = default) => Task.FromResult<LlmConnection?>(new()
        { Transport = LlmTransport.SelfHost, Endpoint = "gemma", Version = "1" });
        public Task SaveCredentialAsync(string connectionId, string credentialJson, string expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
