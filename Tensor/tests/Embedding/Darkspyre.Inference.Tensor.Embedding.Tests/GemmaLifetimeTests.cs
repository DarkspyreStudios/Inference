using System.Security.Cryptography;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Embedding.Gemma;

namespace Darkspyre.Inference.Tensor.Embedding.Tests;

public sealed class GemmaLifetimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gemma-lifetime-" + Guid.NewGuid().ToString("N"));
    private static readonly TensorModelKey Key = GemmaEmbeddingModels.EmbeddingGemma2.Key;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CancellationAfterLoadReleasesTheReturnedModelAndPack()
    {
        using var cancellation = new CancellationTokenSource();
        using var packs = await InstalledAsync();
        var model = new Model();
        await using var engine = new GemmaEmbeddingInference(packs, new Loader(model, cancellation.Cancel), TensorBackendKind.Cpu);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAndWaitAsync(Key, cancellation.Token));
        Assert.Equal(1, model.Disposals);
        await packs.RemoveAsync(Key);
        Assert.Equal(TensorModelPackState.NotInstalled, (await packs.GetStatusAsync(Key)).State);
    }

    [Fact]
    public async Task UrgentStopWaitsForTheNativeCallBeforeReleasingOwnership()
    {
        using var packs = await InstalledAsync();
        var model = new Model { Hold = true };
        await using var engine = new GemmaEmbeddingInference(packs, new Loader(model), TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(Key);
        Task<EmbeddingResult> request = engine.EmbedAsync(new() { ModelId = Key.ModelId, Inputs = [new TextEmbeddingInput("text")] });
        await model.Entered.Task.WaitAsync(Timeout);
        using var urgency = new CancellationTokenSource();
        try
        {
            await engine.StopAsync(urgency.Token);
            Task disposal = engine.DisposeAsync().AsTask();
            urgency.Cancel();
            await model.Cancelled.Task.WaitAsync(Timeout);
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, model.Disposals);
            await Assert.ThrowsAsync<TensorModelPackException>(() => packs.RemoveAsync(Key));
            model.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            await disposal.WaitAsync(Timeout);
            Assert.Equal(1, model.Disposals);
            await packs.RemoveAsync(Key);
        }
        finally { model.Release.TrySetResult(); }
    }

    [Fact]
    public async Task FailedReleaseRetainsTheLeaseUntilExplicitUnloadSucceeds()
    {
        using var packs = await InstalledAsync();
        var model = new Model { ReleaseError = new IOException("native release failed") };
        await using var engine = new GemmaEmbeddingInference(packs, new Loader(model), TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(Key);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => engine.UnloadAndWaitAsync());
            await Assert.ThrowsAsync<TensorModelPackException>(() => packs.RemoveAsync(Key));
            InferenceException error = await Assert.ThrowsAsync<InferenceException>(() => engine.LoadAsync(Key));
            Assert.Equal(InferenceFailureCode.BackendUnavailable, error.Code);
        }
        finally { model.ReleaseError = null; }
        await engine.UnloadAndWaitAsync();
        await packs.RemoveAsync(Key);
        Assert.Equal(2, model.Disposals);
    }

    private async Task<TensorModelPackManager> InstalledAsync()
    {
        Directory.CreateDirectory(_root);
        var artifacts = new List<TensorModelArtifactDefinition>();
        foreach (var artifact in GemmaEmbeddingModels.EmbeddingGemma2.Artifacts)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(artifact.Role);
            string path = Path.Combine(_root, artifact.RelativePath);
            await File.WriteAllBytesAsync(path, bytes);
            artifacts.Add(artifact with { Length = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Sources = [new Uri(path)] });
        }
        var packs = new TensorModelPackManager(new TensorModelCatalog([GemmaEmbeddingModels.EmbeddingGemma2 with { Artifacts = artifacts }]), Path.Combine(_root, "packs"));
        await packs.InstallAsync(Key);
        return packs;
    }

    private sealed class Loader(Model model, Action? returned = null) : IGemmaEmbeddingLoader
    {
        public Task<IGemmaEmbeddingModel> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend, CancellationToken token)
        { returned?.Invoke(); return Task.FromResult<IGemmaEmbeddingModel>(model); }
    }

    private sealed class Model : IGemmaEmbeddingModel
    {
        public bool Hold { get; init; }
        public Exception? ReleaseError { get; set; }
        public int Disposals { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GemmaEmbeddingRun> EmbedAsync(EmbeddingRequest request, CancellationToken token)
        {
            using var registration = token.Register(() => Cancelled.TrySetResult());
            Entered.TrySetResult();
            if (Hold) await Release.Task;
            token.ThrowIfCancellationRequested();
            return new([new float[768]], [], 1);
        }
        public void Dispose() { Disposals++; if (ReleaseError is { } error) throw error; }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
