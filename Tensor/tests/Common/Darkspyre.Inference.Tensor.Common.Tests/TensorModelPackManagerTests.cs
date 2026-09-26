using System.Net;
using System.Security.Cryptography;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorModelPackManagerTests : IDisposable
{
    private readonly string _packDirectory = Path.Combine(
        Path.GetTempPath(),
        $"darkspyre-tensor-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task RegistrationIsImmutableIdempotentAndSharedWithManager()
    {
        var catalog = new TensorModelCatalog([]);
        using var manager = new TensorModelPackManager(catalog, _packDirectory);
        TensorModelPackDefinition definition = Definition("weights"u8.ToArray());
        var sources = new List<Uri>(definition.Artifacts[0].Sources);
        var artifacts = new List<TensorModelArtifactDefinition> { definition.Artifacts[0] with { Sources = sources } };
        var backends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu };
        TensorModelPackDefinition registered = await manager.RegisterAsync(definition with { Artifacts = artifacts, SupportedBackends = backends });
        sources.Clear();
        artifacts.Clear();
        backends.Clear();
        Assert.Single(registered.Artifacts[0].Sources);
        Assert.Contains(TensorBackendKind.Cpu, registered.SupportedBackends);
        Assert.Same(registered, catalog.Register(definition));
        Assert.Same(registered, await manager.GetRequiredDefinitionAsync(definition.Key));
        Assert.Same(registered, Assert.Single(await manager.GetDefinitionsAsync()));
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
        using var manager = new TensorModelPackManager(new([definition]), _packDirectory, http);
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
        using var manager = new TensorModelPackManager(new([definition]), _packDirectory, http);
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
        bool fail = false;
        using var http = new HttpClient(new CallbackHandler((_, _) => Task.FromResult(new HttpResponseMessage(fail ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
        { Content = new ByteArrayContent(weights) })));
        using var manager = new TensorModelPackManager(new([definition]), _packDirectory, http);
        await manager.InstallAsync(definition.Key);
        await using TensorModelPackLease lease = await manager.AcquireAsync(definition.Key);
        File.Delete(lease.GetRequiredArtifact("weights"));
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
        using var manager = new TensorModelPackManager(new([definition]), _packDirectory, http,
            sourceAccess: (origin, _) =>
            {
                Assert.Equal(new Uri("http://127.0.0.1/"), origin);
                return Task.FromResult<TensorSourceCredential?>(TensorSourceCredential.Bearer("protected-token"));
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
        using var manager = new TensorModelPackManager(new([definition]), _packDirectory, http,
            sourceAccess: (_, _) => Task.FromResult<TensorSourceCredential?>(TensorSourceCredential.Basic("stonks", "p:ss wörd")));

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
    public async Task InstallVerifyAndAcquireReturnArtifactFilesInsideThePackDirectory()
    {
        byte[] weights = "model-weights"u8.ToArray();
        TensorModelPackDefinition definition = Definition(weights);
        using var http = new HttpClient(new BytesHandler(weights));
        using var manager = new TensorModelPackManager(
            new TensorModelCatalog([definition]),
            _packDirectory,
            http);

        Assert.Equal(
            TensorModelPackState.NotInstalled,
            (await manager.GetStatusAsync(definition.Key)).State);

        await manager.InstallAsync(definition.Key);

        Assert.Equal(
            TensorModelPackState.Installed,
            (await manager.VerifyAsync(definition.Key)).State);
        await using TensorModelPackLease lease = await manager.AcquireAsync(definition.Key);
        string artifact = lease.GetRequiredArtifact("weights");
        Assert.True(Path.IsPathFullyQualified(artifact));
        Assert.StartsWith(Path.GetFullPath(_packDirectory) + Path.DirectorySeparatorChar, artifact, StringComparison.Ordinal);
        Assert.Equal(weights, await File.ReadAllBytesAsync(artifact));
        await Assert.ThrowsAsync<TensorModelPackException>(() => manager.RemoveAsync(definition.Key));
    }

    [Fact]
    public async Task HashMismatchNeverPublishesAnInstalledManifest()
    {
        byte[] expected = "expected"u8.ToArray();
        byte[] received = "received"u8.ToArray();
        TensorModelPackDefinition definition = Definition(expected);
        using var http = new HttpClient(new BytesHandler(received));
        using var manager = new TensorModelPackManager(
            new TensorModelCatalog([definition]),
            _packDirectory,
            http);

        await Assert.ThrowsAsync<TensorModelPackException>(() => manager.InstallAsync(definition.Key));

        Assert.Equal(
            TensorModelPackState.NotInstalled,
            (await manager.GetStatusAsync(definition.Key)).State);
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
        if (Directory.Exists(_packDirectory))
        {
            Directory.Delete(_packDirectory, recursive: true);
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

    [Fact]
    public async Task DownloadsStageInsideThePackDirectoryAndLeaveNothingBehind()
    {
        byte[] weights = "weights"u8.ToArray();
        TensorModelPackDefinition definition = Definition(weights);
        string staging = Path.Combine(_packDirectory, ".tensor-staging");
        var observed = new List<string>();
        using var http = new HttpClient(new StagingProbeHandler(weights, () => observed.AddRange(Directory.GetFiles(staging))));
        using var manager = new TensorModelPackManager(new([definition]), _packDirectory, http);

        await manager.InstallAsync(definition.Key);

        // While the body streams, its file is in the pack directory's staging folder.
        Assert.Single(observed);
        Assert.Empty(Directory.GetFiles(staging));
        Assert.Equal(TensorModelPackState.Installed, (await manager.VerifyAsync(definition.Key)).State);
    }

    [Fact]
    public async Task AnInstallRemovesAbandonedStagingButKeepsRecentStaging()
    {
        byte[] weights = "weights"u8.ToArray();
        TensorModelPackDefinition definition = Definition(weights);
        string staging = Path.Combine(_packDirectory, ".tensor-staging");
        Directory.CreateDirectory(staging);
        string abandoned = Path.Combine(staging, "abandoned.tmp"), recent = Path.Combine(staging, "recent.tmp");
        await File.WriteAllBytesAsync(abandoned, [1]);
        await File.WriteAllBytesAsync(recent, [1]);
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-2));
        using var http = new HttpClient(new BytesHandler(weights));
        using var manager = new TensorModelPackManager(new([definition]), _packDirectory, http);

        await manager.InstallAsync(definition.Key);

        Assert.False(File.Exists(abandoned));
        Assert.True(File.Exists(recent)); // Another process may still be writing it.
    }

    // Calls back when the body is first read, which is while the download writes its staging file.
    private sealed class StagingProbeHandler(byte[] content, Action probe) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ProbeStream(content, probe)),
                RequestMessage = request
            });
    }

    private sealed class ProbeStream(byte[] content, Action probe) : MemoryStream(content)
    {
        private bool _probed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_probed && Position > 0) { _probed = true; probe(); }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

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
}
