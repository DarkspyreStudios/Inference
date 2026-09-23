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
    public void RegistrationIsImmutableIdempotentAndSharedWithManager()
    {
        var catalog = new TensorModelCatalog([]);
        using var manager = new TensorModelPackManager(catalog, new MemoryPersistenceStore());
        TensorModelPackDefinition definition = Definition("weights"u8.ToArray());
        var sources = new List<Uri>(definition.Artifacts[0].Sources);
        var artifacts = new List<TensorModelArtifactDefinition> { definition.Artifacts[0] with { Sources = sources } };
        var backends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu };
        TensorModelPackDefinition registered = manager.Register(definition with { Artifacts = artifacts, SupportedBackends = backends });
        sources.Clear();
        artifacts.Clear();
        backends.Clear();
        Assert.Single(registered.Artifacts[0].Sources);
        Assert.Contains(TensorBackendKind.Cpu, registered.SupportedBackends);
        Assert.Same(registered, catalog.Register(definition));
        Assert.Same(registered, manager.GetRequiredDefinition(definition.Key));
        Assert.Same(registered, Assert.Single(manager.Definitions));
        Assert.Throws<ArgumentException>(() => catalog.Register(definition with { Artifacts = [definition.Artifacts[0] with { Length = 1000 }] }));
    }

    [Fact]
    public async Task ConcurrentIdenticalRegistrationsPublishOneDefinition()
    {
        var catalog = new TensorModelCatalog([]);
        TensorModelPackDefinition definition = Definition("weights"u8.ToArray());
        TensorModelPackDefinition[] snapshots = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => catalog.Register(definition))));
        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
        Assert.Single(catalog.Definitions);
    }

    [Fact]
    public async Task ProgressIncludesTotalsAndObserverCannotChangeOutcome()
    {
        byte[] weights = "weights"u8.ToArray();
        TensorModelPackDefinition definition = Definition(weights);
        using var http = new HttpClient(new BytesHandler(weights));
        using var manager = new TensorModelPackManager(new([definition]), new MemoryPersistenceStore(), http);
        var progress = new CaptureProgress(throws: true);
        await manager.InstallAsync(definition.Key, progress);
        Assert.Contains(progress.Values, value => value.Stage == TensorModelInstallStage.Downloading);
        Assert.Equal(TensorModelInstallStage.Completed, progress.Values[^1].Stage);
        Assert.Equal(weights.Length, progress.Values[^1].OverallBytesTransferred);
        Assert.Equal(weights.Length, progress.Values[^1].OverallTotalBytes);
        Assert.All(progress.Values, value => Assert.False(value.SupportsResume));
        Assert.Equal(TensorModelPackState.Installed, (await manager.VerifyAsync(definition.Key)).State);
    }

    [Fact]
    public async Task FailedAndCancelledInstallsHaveTerminalProgress()
    {
        TensorModelPackDefinition definition = Definition("expected"u8.ToArray());
        using var http = new HttpClient(new BytesHandler("too-many-bytes"u8.ToArray()));
        using var manager = new TensorModelPackManager(new([definition]), new MemoryPersistenceStore(), http);
        var progress = new CaptureProgress();
        await Assert.ThrowsAsync<TensorModelPackException>(() => manager.InstallAsync(definition.Key, progress));
        Assert.Equal(TensorModelInstallStage.Failed, progress.Values[^1].Stage);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.InstallAsync(definition.Key, progress, new CancellationToken(true)));
        Assert.Equal(TensorModelInstallStage.Cancelled, progress.Values[^1].Stage);
        Assert.Equal(TensorModelPackState.NotInstalled, (await manager.GetStatusAsync(definition.Key)).State);
    }

    [Fact]
    public async Task InterruptedRepairRemovesOldManifestAndActiveLeasePreventsRepair()
    {
        byte[] weights = "weights"u8.ToArray();
        TensorModelPackDefinition definition = Definition(weights);
        var store = new MemoryPersistenceStore();
        bool fail = false;
        using var http = new HttpClient(new CallbackHandler((_, _) => Task.FromResult(new HttpResponseMessage(fail ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
        { Content = new ByteArrayContent(weights) })));
        using var manager = new TensorModelPackManager(new([definition]), store, http);
        await manager.InstallAsync(definition.Key);
        await using TensorModelPackLease lease = await manager.AcquireAsync(definition.Key);
        var artifact = lease.GetRequiredArtifact("weights");
        await store.DeleteAsync(artifact.StoreName, artifact.AssetName);
        await Assert.ThrowsAsync<TensorModelPackException>(() => manager.InstallAsync(definition.Key));
        await lease.DisposeAsync();
        fail = true;
        TensorModelPackException exception = await Assert.ThrowsAsync<TensorModelPackException>(() => manager.InstallAsync(definition.Key));
        Assert.Equal(TensorSourceState.AccessDenied, Assert.IsType<TensorSourceException>(exception.InnerException).State);
        Assert.Equal(TensorModelPackState.NotInstalled, (await manager.GetStatusAsync(definition.Key)).State);
    }

    [Fact]
    public async Task ProtectedInstallUsesCallbackAndStripsAuthorizationAtRedirect()
    {
        byte[] weights = "weights"u8.ToArray();
        TensorModelPackDefinition definition = Definition(weights);
        int calls = 0;
        using var http = new HttpClient(new CallbackHandler((request, _) =>
        {
            if (++calls == 1)
            {
                Assert.Equal("protected-token", request.Headers.Authorization?.Parameter);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://cdn.example.test/weights") } });
            }
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(weights) });
        }));
        using var manager = new TensorModelPackManager(new([definition]), new MemoryPersistenceStore(), http,
            sourceAccess: (origin, _) =>
            {
                Assert.Equal(new Uri("http://127.0.0.1/"), origin);
                return ValueTask.FromResult<TensorSourceCredential?>(TensorSourceCredential.Bearer("protected-token"));
            });
        await Task.WhenAll(manager.InstallAsync(definition.Key), manager.InstallAsync(definition.Key));
        Assert.Equal(2, calls); // The second installation verifies the first, without downloading again.
    }

    [Fact]
    public async Task ProtectedInstallSendsBasicCredentialsOnlyToTheSourceOrigin()
    {
        byte[] weights = "weights"u8.ToArray();
        TensorModelPackDefinition definition = Definition(weights);
        var schemes = new List<string?>();
        var parameters = new List<string?>();
        int calls = 0;
        using var http = new HttpClient(new CallbackHandler((request, _) =>
        {
            schemes.Add(request.Headers.Authorization?.Scheme);
            parameters.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(++calls == 1
                ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://cdn.example.test/weights") } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(weights) });
        }));
        using var manager = new TensorModelPackManager(new([definition]), new MemoryPersistenceStore(), http,
            sourceAccess: (_, _) => ValueTask.FromResult<TensorSourceCredential?>(TensorSourceCredential.Basic("stonks", "p:ss wörd")));

        await manager.InstallAsync(definition.Key);

        Assert.Equal(["Basic", null], schemes);
        Assert.Equal(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("stonks:p:ss wörd")), parameters[0]);
        Assert.Null(parameters[1]);
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("user:name", "password")]
    [InlineData("user\n", "password")]
    [InlineData("user", "pass\u0000word")]
    public void BasicCredentialsRejectValuesThatCannotFormAHeader(string username, string password)
    {
        Assert.ThrowsAny<ArgumentException>(() => TensorSourceCredential.Basic(
            System.Text.RegularExpressions.Regex.Unescape(username), System.Text.RegularExpressions.Regex.Unescape(password)));
    }

    [Fact]
    public void BasicCredentialsAreRedacted()
    {
        string text = TensorSourceCredential.Basic("stonks", "secret-password").ToString();

        Assert.DoesNotContain("secret-password", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stonks", text, StringComparison.Ordinal);
    }

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

    private sealed class CaptureProgress(bool throws = false) : IProgress<TensorModelInstallProgress>
    {
        public List<TensorModelInstallProgress> Values { get; } = [];
        public void Report(TensorModelInstallProgress value) { Values.Add(value); if (throws) { throw new InvalidOperationException(); } }
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    internal sealed class MemoryPersistenceStore : IPersistenceStore
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
