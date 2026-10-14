using System.Security.Cryptography;
using System.Text.Json;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.STT.Whisper;
using Darkspyre.Persist;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorInstalledPacksTests : IDisposable
{
    private const string Store = "Family";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"darkspyre-installed-packs-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public async Task PacksWithMissingShortOrMismatchedFilesAreNotReported()
    {
        TensorModelPackDefinition complete = await InstallAsync("test/complete", "complete weights");
        TensorModelPackDefinition missing = await InstallAsync("test/missing", "missing weights");
        TensorModelPackDefinition shorter = await InstallAsync("test/shorter", "shorter weights");
        TensorModelPackDefinition mismatched = await InstallAsync("test/mismatched", "mismatched weights");
        File.Delete(ArtifactPath(missing));
        await using (var file = new FileStream(ArtifactPath(shorter), FileMode.Open)) { file.SetLength(file.Length - 1); }
        TensorModelPackDefinition disagreeing = mismatched with
        {
            Artifacts = [mismatched.Artifacts[0] with { Sha256 = new string('0', 64) }]
        };
        await File.WriteAllTextAsync(StorePath(TensorModelPackManager.DescriptionName(mismatched.Key)),
            JsonSerializer.Serialize(disagreeing, TensorJson.ModelPack));

        IReadOnlyList<TensorModelPackDefinition> reported = await ReportAsync([]);

        Assert.Equal(complete.Key, Assert.Single(reported).Key);
    }

    [Fact]
    public async Task BuiltInsAreMatchedByKeyAndHashWithoutADescription()
    {
        TensorModelPackDefinition builtIn = await InstallAsync("test/built-in", "built-in weights");
        File.Delete(StorePath(TensorModelPackManager.DescriptionName(builtIn.Key)));
        TensorModelPackDefinition installedOther = await InstallAsync("test/changed", "other weights");
        TensorModelPackDefinition declared = Definition("test/changed", "declared weights", "unused.gguf");

        IReadOnlyList<TensorModelPackDefinition> reported = await ReportAsync([builtIn, declared]);

        Assert.Same(builtIn, Assert.Single(reported));
        Assert.NotEqual(declared.Artifacts[0].Sha256, installedOther.Artifacts[0].Sha256);
    }

    [Fact]
    public async Task WhisperReportsAnImportedPublishedVariantByItsHeader()
    {
        TensorModelPackDefinition tiny = TensorSpeechModels.Whisper.Single(model => model.Key.ModelId == "whisper/tiny");
        TensorModelPackDefinition imported = tiny with { Key = new("imported/ggml-tiny.bin", "test") };
        var provider = new WhisperEngineProvider();
        var modelStore = new OsUserPersist(_root);
        await WriteWhisperPackAsync(provider.Definition.ModelStoreKey, imported, magic: 0x67676d6c);

        TensorModelPackDefinition reported = Assert.Single(await provider.GetInstalledModelsAsync(modelStore));
        Assert.Equal(imported.Key, reported.Key);

        await WriteWhisperPackAsync(provider.Definition.ModelStoreKey, imported, magic: 0x12345678);
        Assert.Empty(await provider.GetInstalledModelsAsync(modelStore));
    }

    [Fact]
    public async Task TheListingTellsInstalledFilesMissingAndNotDownloadedApart()
    {
        TensorModelPackDefinition absent = Definition("test/absent", "absent weights", "unused.gguf");
        TensorModelPackDefinition installed = await InstallAsync("test/installed", "installed weights");
        TensorModelPackDefinition deleted = await InstallAsync("test/deleted", "deleted weights");
        TensorModelPackDefinition shorter = await InstallAsync("test/shorter", "shorter weights");
        TensorModelPackDefinition unparseable = await InstallAsync("test/unparseable", "unparseable weights");
        File.Delete(ArtifactPath(deleted));
        await using (var file = new FileStream(ArtifactPath(shorter), FileMode.Open)) { file.SetLength(file.Length - 1); }
        await File.WriteAllTextAsync(StorePath(TensorModelPackManager.ManifestName(unparseable.Key)), "{ not json");

        TensorModelListing listing = await ListAsync([absent, installed, deleted, shorter, unparseable], [installed], saved: null);

        Assert.Equal("the store", listing.Store);
        Assert.Null(listing.Saved);
        Assert.Equal(
            [
                (absent.Key, SetupInstallState.NotDownloaded, (string?)null),
                (installed.Key, SetupInstallState.Installed, null),
                (deleted.Key, SetupInstallState.FilesMissing, "model.gguf"),
                (shorter.Key, SetupInstallState.FilesMissing, "model.gguf"),
                (unparseable.Key, SetupInstallState.NotDownloaded, null)
            ],
            listing.Models.Select(model => (model.Definition.Key, model.State, model.MissingFile)));
    }

    [Fact]
    public async Task AnUnreportedSavedModelIsCarriedApartFromTheOfferedModels()
    {
        TensorModelPackDefinition builtIn = Definition("test/built-in", "built-in weights", "unused.gguf");
        TensorModelPackDefinition imported = await InstallAsync("test/imported", "imported weights");
        File.Delete(ArtifactPath(imported));

        TensorModelListing missing = await ListAsync([builtIn], [], imported);
        TensorModelListing never = await ListAsync([builtIn], [], Definition("test/never", "never weights", "unused.gguf"));
        TensorModelListing reported = await ListAsync([builtIn], [imported], imported);

        Assert.Equal(builtIn.Key, Assert.Single(missing.Models).Definition.Key);
        Assert.Equal((imported.Key, SetupInstallState.FilesMissing, "model.gguf"),
            (missing.Saved!.Definition.Key, missing.Saved.State, missing.Saved.MissingFile));
        Assert.Equal(SetupInstallState.NotDownloaded, never.Saved!.State);
        Assert.Single(never.Models);
        Assert.Null(reported.Saved);
        Assert.Equal(SetupInstallState.Installed, reported.Models.Single(model => model.Definition.Key == imported.Key).State);
    }

    private Task<TensorModelListing> ListAsync(IReadOnlyList<TensorModelPackDefinition> builtIns,
        IReadOnlyList<TensorModelPackDefinition> installed, TensorModelPackDefinition? saved) =>
        TensorInstalledPacks.ListAsync(new OsUserPersist(_root), "the store", Store, builtIns, installed, saved);

    // A sparse file of the tiny variant's length with its GGML header, and the manifest and description of the pack.
    private async Task WriteWhisperPackAsync(string store, TensorModelPackDefinition pack, uint magic)
    {
        string artifact = Path.Combine(_root, store, TensorModelPackManager.ArtifactName(pack.Key, pack.Artifacts[0].RelativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        await using (var file = new FileStream(artifact, FileMode.Create))
        {
            file.SetLength(pack.Artifacts[0].Length);
            await using var writer = new BinaryWriter(file);
            writer.Write(magic);
            // tiny: multilingual vocabulary, 1,500 audio and 448 text positions, width 384, 6 heads, 4 layers, 80 mels, F16.
            foreach (int value in new[] { 51865, 1500, 384, 6, 4, 448, 384, 6, 4, 80, 1 }) { writer.Write(value); }
        }
        await File.WriteAllTextAsync(Path.Combine(_root, store, TensorModelPackManager.ManifestName(pack.Key)),
            JsonSerializer.Serialize(PersistedModelManifest.Create(pack), TensorJsonContext.Default.PersistedModelManifest));
        await File.WriteAllTextAsync(Path.Combine(_root, store, TensorModelPackManager.DescriptionName(pack.Key)),
            JsonSerializer.Serialize(pack, TensorJson.ModelPack));
    }

    private Task<IReadOnlyList<TensorModelPackDefinition>> ReportAsync(IReadOnlyList<TensorModelPackDefinition> builtIns) =>
        TensorInstalledPacks.ReportAsync(new OsUserPersist(_root), Store, builtIns, _ => true, formatCheck: null);

    private async Task<TensorModelPackDefinition> InstallAsync(string id, string content)
    {
        TensorModelPackDefinition definition = Definition(id, content, Path.Combine(_root, "sources", id.Replace('/', '-') + ".gguf"));
        Directory.CreateDirectory(Path.Combine(_root, "sources"));
        await File.WriteAllTextAsync(definition.Artifacts[0].Sources[0].LocalPath, content);
        using var packs = new TensorModelPackManager(new([definition]), Path.Combine(_root, Store));
        await packs.InstallAsync(definition.Key);
        return definition;
    }

    // The source keeps its file scheme when serialized, as an import's local source does.
    private static TensorModelPackDefinition Definition(string id, string content, string source)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new()
        {
            Key = new(id, "revision"),
            Capability = TensorCapability.Llm,
            RuntimeArchitecture = "test",
            License = "test-only",
            SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Cpu },
            Artifacts = [new() { Role = "model", RelativePath = "model.gguf", Length = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Sources = [new Uri(new Uri(Path.GetFullPath(source)).AbsoluteUri)] }]
        };
    }

    private string ArtifactPath(TensorModelPackDefinition pack) =>
        StorePath(TensorModelPackManager.ArtifactName(pack.Key, pack.Artifacts[0].RelativePath));

    private string StorePath(string assetName) => Path.Combine(_root, Store, assetName);
}
