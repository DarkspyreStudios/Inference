using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorLocationImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tensor-location-tests-").FullName;

    [Fact]
    public async Task LocalImportEstablishesIntegrityAndInstallsIntoThePackDirectory()
    {
        string path = await FileAsync("model.bin", "model"u8.ToArray());
        var resolver = new Resolver();
        var selection = new TensorModelSelection(new TensorModelLocation.LocalFile(path), License: "test-license");
        Assert.Null((await resolver.ResolveAsync(selection)).Definition);
        using var manager = new TensorModelPackManager(new([]), Path.Combine(_root, "packs"));
        TensorModelImportResult result = await manager.ImportAsync(new(selection, resolver));
        TensorModelPackDefinition definition = Assert.IsType<TensorModelPackDefinition>(result.Resolution.Definition);
        TensorModelArtifactDefinition artifact = Assert.Single(definition.Artifacts);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("model"u8)), artifact.Sha256);
        Assert.Equal(5, artifact.Length);
        Assert.Null(artifact.Provenance!.Repository);
        Assert.DoesNotContain(_root, definition.Key.ToString(), StringComparison.Ordinal);
        Assert.Equal(TensorModelPackState.Installed, (await manager.VerifyAsync(definition.Key)).State);
        await using TensorModelPackLease lease = await manager.AcquireAsync(definition.Key);
        Assert.Equal("model", await File.ReadAllTextAsync(lease.GetRequiredArtifact("model")));
        TensorModelImportResult duplicate = await manager.ImportAsync(new(selection, resolver));
        Assert.Equal(definition.Key, duplicate.Resolution.Definition!.Key);
        Assert.Single((await manager.GetDefinitionsAsync()));
    }

    [Fact]
    public async Task IgnoredHttpRangesNeverDownloadDuringInspectionButExplicitImportWorks()
    {
        int fullReads = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Headers.Range is null) fullReads++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent("model"u8.ToArray()) };
        }));
        var resolver = new Resolver(new TensorArtifactReader(httpClient: http));
        var selection = new TensorModelSelection(new TensorModelLocation.RemoteFile(new("https://models.test/model.bin")), License: "test");
        Assert.Null((await resolver.ResolveAsync(selection)).Definition);
        Assert.Equal(0, fullReads);
        using var manager = new TensorModelPackManager(new([]), Path.Combine(_root, "packs"), http);
        TensorModelImportResult result = await manager.ImportAsync(new(selection, resolver));
        Assert.Equal(TensorCompatibilityState.Supported, result.Resolution.Evaluation.State);
        Assert.Equal(1, fullReads);
        Assert.Equal(TensorModelPackState.Installed, (await manager.VerifyAsync(result.Resolution.Definition!.Key)).State);
    }

    [Fact]
    public async Task ExpectedHashMismatchAndIncompatibleContentNeverPublishAPack()
    {
        string path = await FileAsync("model.bin", "model"u8.ToArray());
        var resolver = new Resolver();
        using var manager = new TensorModelPackManager(new([]), Path.Combine(_root, "packs"));
        var selection = new TensorModelSelection(new TensorModelLocation.LocalFile(path), License: "test", ExpectedSha256: new('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.ImportAsync(new(selection, resolver)));
        Assert.Empty((await manager.GetDefinitionsAsync()));
        await File.WriteAllTextAsync(path, "invalid");
        TensorModelImportResult rejected = await manager.ImportAsync(new(selection with { ExpectedSha256 = null }, resolver));
        Assert.Equal(TensorCompatibilityState.Unsupported, rejected.Resolution.Evaluation.State);
        Assert.Null(rejected.Resolution.Definition);
        Assert.Empty((await manager.GetDefinitionsAsync()));
    }

    [Fact]
    public async Task FolderSelectionRejectsAmbiguityAndDoesNotScanChildren()
    {
        await FileAsync("a.bin", "model"u8.ToArray());
        await FileAsync("b.bin", "model"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(_root, "child"));
        await File.WriteAllTextAsync(Path.Combine(_root, "child", "c.bin"), "model");
        var resolver = new Resolver();
        var selection = new TensorModelSelection(new TensorModelLocation.LocalFolder(_root, ""), License: "test");
        Assert.Contains((await resolver.DiscoverAsync(selection)).Issues!, issue => issue.Code == "primary-ambiguous");
        var selected = await resolver.DiscoverAsync(selection with { Location = new TensorModelLocation.LocalFolder(_root, "a.bin") });
        Assert.Equal("a.bin", Assert.Single(selected.Value!.Artifacts).Path);
        Assert.Equal(TensorSourceState.InvalidRequest, (await resolver.DiscoverAsync(selection with
        { Location = new TensorModelLocation.LocalFolder(_root, "../escape.bin") })).State);
    }

    [Fact]
    public async Task MissingLicenseAndCancellationDoNotPublish()
    {
        string path = await FileAsync("model.bin", "model"u8.ToArray());
        using var manager = new TensorModelPackManager(new([]), Path.Combine(_root, "packs"));
        var selection = new TensorModelSelection(new TensorModelLocation.LocalFile(path));
        TensorModelImportResult result = await manager.ImportAsync(new(selection, new Resolver()));
        Assert.Contains(result.Resolution.Evaluation.Issues, issue => issue.Code == "license-required");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ImportAsync(new(selection, new Resolver()), cancellationToken: new(true)));
        Assert.Empty((await manager.GetDefinitionsAsync()));
    }

    [Fact]
    public async Task MetadataEditorImportsAndSavesWithoutALiveInstance()
    {
        string path = await FileAsync("model.bin", "model"u8.ToArray());
        var target = new Target();
        using var manager = new TensorModelPackManager(new([]), Path.Combine(_root, "packs"));
        await using var setup = new TensorSetupSession(new(new("test", "Test", Darkspyre.Inference.Common.InferenceImplementationKind.Tensor,
            Darkspyre.Inference.Common.InferenceTask.TextGeneration), "test"), new(SetupChangeIntent.Create, "test", null), target, [], manager, resolver: new Resolver());
        Text(setup, "local-path").SetValue(path);
        Text(setup, "source-license").SetValue("test");
        Assert.True(setup.Snapshot.IsDirty);
        Assert.False((await setup.ValidateAsync()).IsValid);
        Assert.Equal(SetupActionState.Completed, (await setup.Snapshot.Actions.Single(item => item.Descriptor.Id == "import").ExecuteAsync()).State);
        Assert.Equal(SetupCommitState.Committed, (await setup.CommitAsync()).State);
        Assert.NotNull(target.Changes);
        Assert.Equal(SetupObservation.Unknown, setup.Snapshot.Status.Loaded);
        Text(setup, "local-path").SetValue(path + ".different");
        Assert.False((await setup.ValidateAsync()).IsValid);
    }

    [Fact]
    public async Task CancellationDuringCopyDoesNotRegisterOrPublish()
    {
        string path = await FileAsync("model.bin", "model"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        using var manager = new TensorModelPackManager(new([]), Path.Combine(_root, "packs"));
        var selection = new TensorModelSelection(new TensorModelLocation.LocalFile(path), License: "test");
        var progress = new CancelProgress(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ImportAsync(new(selection, new Resolver()), progress, cancellation.Token));
        Assert.Equal(TensorModelInstallStage.Cancelled, progress.Last);
        Assert.Empty((await manager.GetDefinitionsAsync()));
    }

    [Fact]
    public async Task AnArtifactThatCannotBeSavedLeavesThePackNotInstalled()
    {
        string path = await FileAsync("model.bin", "model"u8.ToArray());
        var selection = new TensorModelSelection(new TensorModelLocation.LocalFile(path), License: "test");
        string packs = Path.Combine(_root, "packs-under-test");
        await File.WriteAllBytesAsync(packs, []); // A file occupies the pack directory, so no artifact can be saved.

        using var manager = new TensorModelPackManager(new([]), packs);
        await Assert.ThrowsAnyAsync<IOException>(() => manager.ImportAsync(new(selection, new Resolver())));
        File.Delete(packs);
        TensorModelKey key = Assert.Single((await manager.GetDefinitionsAsync())).Key;
        Assert.Equal(TensorModelPackState.NotInstalled, (await manager.GetStatusAsync(key)).State);
    }

    [UnixFact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task AManifestThatCannotBeSavedLeavesThePackNotInstalled()
    {
        string path = await FileAsync("model.bin", "model"u8.ToArray());
        var selection = new TensorModelSelection(new TensorModelLocation.LocalFile(path), License: "test");
        string probe = Path.Combine(_root, "probe");
        string packs = Path.Combine(_root, "packs-under-test");
        string packFolder;
        using (var probeManager = new TensorModelPackManager(new([]), probe))
        {
            // An unobstructed import reveals where this pack's folder and manifest go.
            TensorModelImportResult probed = await probeManager.ImportAsync(new(selection, new Resolver()));
            await using TensorModelPackLease lease = await probeManager.AcquireAsync(probed.Resolution.Definition!.Key);
            string relative = Path.GetRelativePath(probe, Path.GetDirectoryName(Path.GetDirectoryName(lease.GetRequiredArtifact("model"))!)!);
            packFolder = Path.Combine(packs, relative);
        }
        // Artifacts can still be written into artifacts/, but the manifest cannot be added to the pack folder.
        Directory.CreateDirectory(Path.Combine(packFolder, "artifacts"));
        File.SetUnixFileMode(packFolder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var manager = new TensorModelPackManager(new([]), packs);
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => manager.ImportAsync(new(selection, new Resolver())));
            TensorModelKey key = Assert.Single((await manager.GetDefinitionsAsync())).Key;
            Assert.Equal(TensorModelPackState.NotInstalled, (await manager.GetStatusAsync(key)).State);
        }
        finally
        {
            File.SetUnixFileMode(packFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task ReaderValidatesRangesAndRejectsInconsistentTotals()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent([1]) { Headers = { ContentRange = new ContentRangeHeaderValue(0, 0, 99) } } }));
        var reader = new TensorArtifactReader(httpClient: http);
        var artifact = new TensorSourceArtifact("model.bin", 100, null, new(new("https://models.test/"), null, null, "model.bin", "test"), new("https://models.test/model.bin"));
        Assert.Equal(TensorSourceState.Unverified, (await reader.ReadMetadataAsync(artifact, new(0, 1))).State);
        Assert.Equal(TensorSourceState.InvalidRequest, (await reader.ReadMetadataAsync(artifact, new(long.MaxValue, 2))).State);
    }

    private static SetupTextField Text(TensorSetupSession setup, string id) => Assert.IsType<SetupTextField>(setup.Snapshot.Steps.SelectMany(step => step.Sections).SelectMany(section => section.Fields).Single(field => field.Description.Id == id));
    private async Task<string> FileAsync(string name, byte[] bytes)
    { string path = Path.Combine(_root, name); await File.WriteAllBytesAsync(path, bytes); return path; }
    public void Dispose() => Directory.Delete(_root, true);

    private sealed class Target : ISetupCommitTarget<TensorSetupChanges>
    {
        public TensorSetupChanges? Changes { get; private set; }
        public Task<SetupCommitResult> CommitAsync(TensorSetupChanges changes, CancellationToken cancellationToken = default)
        { Changes = changes; return Task.FromResult(new SetupCommitResult(SetupCommitState.Committed, "v1")); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<TensorModelInstallProgress>
    {
        public TensorModelInstallStage Last { get; private set; }
        public void Report(TensorModelInstallProgress value)
        { Last = value.Stage; if (value.Stage == TensorModelInstallStage.Downloading) cancellation.Cancel(); }
    }
    private sealed class Resolver(ITensorArtifactReader? reader = null) : TensorModelResolver(reader: reader)
    {
        public override IReadOnlyList<TensorArtifactRequirement> Requirements => [new("model", "*.bin")];
        public override async Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate, TensorBackendKind backend, ITensorArtifactReader reader, CancellationToken cancellationToken = default)
        {
            TensorSourceArtifact artifact = Assert.Single(candidate.Artifacts);
            if (artifact.Sha256 is null || artifact.Length is null) return new(new(TensorCompatibilityState.Unverified, [], []));
            var content = await reader.ReadMetadataAsync(artifact, new(0, 5), cancellationToken);
            if (content.Value is null || !content.Value.Bytes.Span.SequenceEqual("model"u8)) return new(new(TensorCompatibilityState.Unsupported, [], []));
            return new(new(TensorCompatibilityState.Supported, [], []), TensorResolvedPack.Create(candidate, TensorCapability.Llm, "test",
                new HashSet<TensorBackendKind> { TensorBackendKind.Cpu }, [TensorResolvedPack.Artifact(artifact, "model", artifact.Path)]));
        }
    }
}
