using System.Security.Cryptography;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Models.Embeddings;

namespace Darkspyre.Inference.Tensor.Embedding.Tests;

public sealed class EmbeddingCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "embedding-cleanup-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TextOnlyModelRejectsAMixedBatchBeforeEmbeddingAndRemainsUsable()
    {
        var model = new Model();
        using TensorModelPackManager packs = await InstalledAsync();
        await using var engine = Engine(packs, model);
        await engine.LoadAndWaitAsync(Key);
        EmbeddingCapabilities capabilities = await engine.GetCapabilitiesAsync();
        Assert.Equal(EmbeddingModality.Text, Assert.Single(capabilities.Modalities));

        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => engine.EmbedAsync(new()
        {
            ModelId = Key.ModelId,
            Inputs = [new TextEmbeddingInput("reference"), new MediaEmbeddingInput(new("image/png", new byte[] { 1 }))]
        }));
        Assert.Equal(InferenceFailureCode.UnsupportedFeature, error.Code);
        Assert.False(model.Entered.Task.IsCompleted);

        EmbeddingResult result = await engine.EmbedAsync(new()
        {
            ModelId = Key.ModelId,
            Inputs = [new TextEmbeddingInput("reference")]
        });
        Assert.Single(result.Vectors);
        Assert.Equal(1024, result.Dimensions);
    }

    [Fact]
    public async Task ReturnedModelIsReleasedAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new Model();
        using TensorModelPackManager packs = await InstalledAsync();
        await using var engine = Engine(packs, model, cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAndWaitAsync(Key, cancellation.Token));

        Assert.Equal(1, model.Disposals);
        await packs.RemoveAsync(Key);
        Assert.NotEqual(TensorModelPackState.Installed, (await packs.GetStatusAsync(Key)).State);
    }

    [Fact]
    public async Task UrgentStopDrainsIgnoringEmbeddingBeforeReleasingItsPack()
    {
        var model = new Model { HoldEmbedding = true };
        using TensorModelPackManager packs = await InstalledAsync();
        var engine = Engine(packs, model);
        await engine.LoadAndWaitAsync(Key);
        using var urgency = new CancellationTokenSource();
        Task<EmbeddingResult> embedding = engine.EmbedAsync(new() { ModelId = Key.ModelId, Inputs = [new TextEmbeddingInput("reference")] });
        await model.Entered.Task.WaitAsync(Timeout);
        try
        {
            await engine.StopAsync(urgency.Token);
            Task disposal = engine.DisposeAsync().AsTask();
            urgency.Cancel();
            await model.Canceled.Task.WaitAsync(Timeout);
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, model.Disposals);
            await Assert.ThrowsAsync<TensorModelPackException>(() => packs.RemoveAsync(Key));

            model.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => embedding);
            await disposal.WaitAsync(Timeout);
            await packs.RemoveAsync(Key);
            Assert.Equal(1, model.Disposals);
        }
        finally
        {
            model.Release.TrySetResult();
            await engine.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
    }

    private static TensorModelKey Key => new("host/embedding-cleanup", "reference");

    private async Task<TensorModelPackManager> InstalledAsync()
    {
        Directory.CreateDirectory(_root);
        byte[] bytes = "controlled checkpoint bytes"u8.ToArray();
        string source = Path.Combine(_root, "source.gguf");
        await File.WriteAllBytesAsync(source, bytes);
        TensorModelPackDefinition definition = new()
        {
            Key = Key,
            Capability = TensorCapability.Embedding,
            RuntimeArchitecture = TensorEmbeddingModels.BertGguf.Name,
            License = "Apache-2.0",
            SupportedBackends = TensorEmbeddingModels.BertGguf.Backends,
            Artifacts = [new() { Role = "model", RelativePath = "model.gguf", Length = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Sources = [new(source)] }]
        };
        var packs = new TensorModelPackManager(new([definition]), Path.Combine(_root, "packs"));
        await packs.InstallAsync(Key);
        return packs;
    }

    private static TensorEmbeddingInference Engine(TensorModelPackManager packs, Model model, Action? afterLoad = null) =>
        new(packs, new Loader(model, afterLoad), TensorBackendKind.Cpu);

    private sealed class Loader(Model model, Action? afterLoad) : IEmbeddingModelLoader
    {
        public Task<IEmbeddingModel> LoadAsync(TensorModelPackDefinition definition, TensorEmbeddingArchitecture architecture,
            TensorModelPackLease lease, TensorBackendKind backend, CancellationToken token)
        {
            afterLoad?.Invoke();
            return Task.FromResult<IEmbeddingModel>(model);
        }
    }

    private sealed class Model : IEmbeddingModel
    {
        public string ModelName => Key.ModelId;
        public string Architecture => "bert";
        public int Dimensions => 1024;
        public int MaxTokens => 8192;
        public int VocabularySize => 10;
        public int Disposals { get; private set; }
        public bool HoldEmbedding { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int[] Tokenize(string text, bool truncate = false) => [1];

        public async Task<EmbeddingBatchResult> EmbedTokensAsync(IReadOnlyList<int[]> inputs, CancellationToken cancellationToken = default)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() => Canceled.TrySetResult());
            Entered.TrySetResult();
            if (HoldEmbedding) await Release.Task;
            return new(inputs.Select(_ => Enumerable.Repeat(1f, Dimensions).ToArray()).ToArray(), inputs.Count);
        }

        public void Dispose() => Disposals++;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
