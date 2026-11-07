using System.Security.Cryptography;
using System.Text.Json;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Tensor.STT.Whisper;

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

    private Task<TensorModelListing> ListAsync(IReadOnlyList<TensorModelPackDefinition> builtIns,
        IReadOnlyList<TensorModelPackDefinition> installed, TensorModelPackDefinition? saved) =>
        ListOverPacksAsync(builtIns, new(installed, []), saved);

    private async Task<TensorModelListing> ListOverPacksAsync(IReadOnlyList<TensorModelPackDefinition> builtIns,
        TensorInstalledReport installed, TensorModelPackDefinition? saved)
    {
        using var packs = new TensorModelPackManager(new([]), Path.Combine(_root, Store));
        return await TensorInstalledPacks.ListAsync(packs, "the store", builtIns, installed, saved);
    }

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
