using System.Net;
using System.Security.Cryptography;
using Darkspyre.Persistence;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorModelPackManagerTests : IDisposable
{
    private readonly string _persistenceRoot = Path.Combine(
        Path.GetTempPath(),
        $"darkspyre-tensor-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task InstallVerifyAndAcquireUseAnyApplicationPersistenceStore()
    {
        byte[] weights = "model-weights"u8.ToArray();
        TensorModelPackDefinition definition = Definition(weights);
        var persistence = new MemoryPersistenceStore();
        using var http = new HttpClient(new BytesHandler(weights));
        using var manager = new TensorModelPackManager(
            new TensorModelCatalog([definition]),
            persistence,
            http);

        Assert.Equal(
            TensorModelPackState.NotInstalled,
            (await manager.GetStatusAsync(definition.Key)).State);

        await manager.InstallAsync(definition.Key);

        Assert.Equal(
            TensorModelPackState.Installed,
            (await manager.VerifyAsync(definition.Key)).State);
        await using TensorModelPackLease lease = await manager.AcquireAsync(definition.Key);
        TensorSharp.Runtime.PersistenceFileReference artifact = lease.GetRequiredArtifact("weights");
        Assert.Same(persistence, artifact.Store);
        Assert.Equal(TensorModelPackManager.DefaultStoreName, artifact.StoreName);
        await using Stream? storedWeights = await artifact.Store.OpenReadAsync(
            artifact.StoreName,
            artifact.AssetName);
        Assert.NotNull(storedWeights);
        using var copiedWeights = new MemoryStream();
        await storedWeights.CopyToAsync(copiedWeights);
        Assert.Equal(weights, copiedWeights.ToArray());
        await Assert.ThrowsAsync<TensorModelPackException>(() => manager.RemoveAsync(definition.Key));
    }

    [Fact]
    public async Task HashMismatchNeverPublishesAnInstalledManifest()
    {
        byte[] expected = "expected"u8.ToArray();
        byte[] received = "received"u8.ToArray();
        TensorModelPackDefinition definition = Definition(expected);
        var persistence = new MemoryPersistenceStore();
        using var http = new HttpClient(new BytesHandler(received));
        using var manager = new TensorModelPackManager(
            new TensorModelCatalog([definition]),
            persistence,
            http);

        await Assert.ThrowsAsync<TensorModelPackException>(() => manager.InstallAsync(definition.Key));

        Assert.Equal(
            TensorModelPackState.NotInstalled,
            (await manager.GetStatusAsync(definition.Key)).State);
    }

    [Fact]
    public async Task DirectFileStoreExposesOnlyThePersistenceContract()
    {
        var directStore = new TensorDirectFilePersistStore(_persistenceRoot);
        IPersistenceStore applicationStore = Assert.IsAssignableFrom<IPersistenceStore>(directStore);
        byte[] content = "weights"u8.ToArray();
        await using var input = new MemoryStream(content);

        await applicationStore.SaveAsync("models", "pack/model.bin", input);

        Assert.True(await applicationStore.ExistsAsync("models", "pack/model.bin"));
        await using Stream? saved = await applicationStore.OpenReadAsync("models", "pack/model.bin");
        Assert.NotNull(saved);
        Assert.IsType<FileStream>(saved);
        using var copy = new MemoryStream();
        await saved.CopyToAsync(copy);
        Assert.Equal(content, copy.ToArray());
        await using var invalid = new MemoryStream(content);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            applicationStore.SaveAsync("models", "../model.bin", invalid));
    }

    [Fact]
    public async Task ExecutionCoordinatorSerializesTheSameModelAndBackend()
    {
        using var coordinator = new TensorExecutionCoordinator();
        var key = new TensorModelKey("test/model", "1");
        await using IAsyncDisposable first = await coordinator.AcquireAsync(key, TensorBackendKind.Cpu);
        Task<IAsyncDisposable> waiting = coordinator.AcquireAsync(key, TensorBackendKind.Cpu).AsTask();

        Assert.False(waiting.IsCompleted);
        await first.DisposeAsync();
        await using IAsyncDisposable second = await waiting;
    }

    public void Dispose()
    {
        if (Directory.Exists(_persistenceRoot))
        {
            Directory.Delete(_persistenceRoot, recursive: true);
        }
    }

    private static TensorModelPackDefinition Definition(byte[] weights) =>
        new()
        {
            Key = new TensorModelKey("test/model", "revision-1"),
            Capability = TensorCapability.Llm,
            RuntimeArchitecture = "test",
            License = "test-only",
            SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu },
            Artifacts =
            [
                new TensorModelArtifactDefinition
                {
                    Role = "weights",
                    RelativePath = "model.gguf",
                    Length = weights.Length,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(weights)),
                    Sources = [new Uri("http://127.0.0.1/model.gguf")]
                }
            ]
        };

    private sealed class BytesHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
                RequestMessage = request
            });
    }

    private sealed class MemoryPersistenceStore : IPersistenceStore
    {
        private readonly Dictionary<(string Store, string Asset), byte[]> _assets = [];
        private readonly Lock _gate = new();

        public Task<bool> ExistsAsync(
            string storeName,
            string assetName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_assets.ContainsKey((storeName, assetName)));
            }
        }

        public Task<Stream?> OpenReadAsync(
            string storeName,
            string assetName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult<Stream?>(_assets.TryGetValue((storeName, assetName), out byte[]? value)
                    ? new MemoryStream(value, writable: false)
                    : null);
            }
        }

        public Task<IReadOnlyList<string>> EnumerateAsync(
            string storeName,
            string assetNamePrefix,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                IReadOnlyList<string> names = _assets.Keys
                    .Where(key => string.Equals(key.Store, storeName, StringComparison.Ordinal)
                                  && key.Asset.StartsWith(assetNamePrefix, StringComparison.Ordinal))
                    .Select(static key => key.Asset)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                return Task.FromResult(names);
            }
        }

        public async Task SaveAsync(
            string storeName,
            string assetName,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _assets[(storeName, assetName)] = copy.ToArray();
            }
        }

        public Task DeleteAsync(
            string storeName,
            string assetName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _assets.Remove((storeName, assetName));
            }

            return Task.CompletedTask;
        }
    }

}
