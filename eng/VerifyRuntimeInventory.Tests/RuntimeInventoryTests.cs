using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace VerifyRuntimeInventory.Tests;

public sealed class RuntimeInventoryTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void RealPackageFilesBindManifestIdentityHashesAndBuildTargetRidAliases()
    {
        string assets = Fixture("Whisper.net.Runtime", "1.9.1", ["build/macos-arm64/libwhisper.dylib"]);
        RuntimeInventory inventory = RuntimeInventory.Read(assets, Commit);
        RuntimeAsset asset = Assert.Single(inventory.NativeAssets);
        Assert.Equal("osx-arm64", asset.Rid);
        Assert.Equal("NativeLibrary", asset.Kind);
        Assert.Equal("NotInspected", asset.ArchitectureValidation);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("fixture bytes"))), asset.Sha256);
        Assert.Equal("NotRun", inventory.RuntimeQualification);
        Assert.Equal(25, inventory.Coverage.Count);
        Assert.Equal("AssetsPresent", inventory.Coverage.Single(item => item.Family == "Whisper" && item.Rid == "osx-arm64").State);
        Assert.Equal("AssetsMissing", inventory.Coverage.Single(item => item.Family == "Whisper" && item.Rid == "win-arm64").State);
    }

    [Fact]
    public void DependencyLibraryDoesNotSubstituteForMissingWhisperEntryPoint()
    {
        RuntimeInventory inventory = RuntimeInventory.Read(Fixture("Whisper.net.Runtime", "1.9.1", ["build/linux-arm64/libggml-whisper.so"]), Commit);
        Assert.Equal("AssetsMissing", inventory.Coverage.Single(item => item.Family == "Whisper" && item.Rid == "linux-arm64").State);
    }

    [Fact]
    public void GgmlPayloadIsAnExplicitManagedClosureFailureSignal()
    {
        RuntimeInventory inventory = RuntimeInventory.Read(Fixture("Darkspyre.TensorSharp.Backends.GGML", "1.0.0", ["runtimes/linux-arm64/native/libGgmlOps.so"]), Commit);
        Assert.True(inventory.HasTransitiveGgmlNatives);
        Assert.Equal("AssetsPresent", inventory.Coverage.Single(item => item.Family == "Ggml" && item.Rid == "linux-arm64").State);
    }

    [Fact]
    public void ManagedAssembliesDoNotCountAsNativePayloads()
    {
        RuntimeInventory inventory = RuntimeInventory.Read(Fixture("Darkspyre.TensorSharp.Backends.GGML", "1.0.0", ["lib/net11.0/TensorSharp.Backends.GGML.dll"]), Commit);
        Assert.Empty(inventory.NativeAssets);
        Assert.False(inventory.HasTransitiveGgmlNatives);
    }

    [Theory]
    [InlineData("Darkspyre.TensorSharp.Backends.GGML.Native.linux-arm64")]
    [InlineData("Darkspyre.TensorSharp.Backends.GGML.Native.linux-arm64.cuda13")]
    public void NativePackageIdentityRejectsManagedClosureWithoutPayload(string id)
    {
        RuntimeInventory inventory = RuntimeInventory.Read(Fixture(id, "1.0.0", []), Commit);
        Assert.Empty(inventory.NativeAssets);
        Assert.True(inventory.HasTransitiveGgmlNatives);
        Assert.Equal("NotRun", inventory.RuntimeQualification);
    }

    [Fact]
    public void OptionalVariantDirectoryBindsItsPackageRidAndHashesActualFiles()
    {
        const string id = "Darkspyre.TensorSharp.Backends.GGML.Native.linux-arm64.cuda13";
        RuntimeInventory inventory = RuntimeInventory.Read(Fixture(id, "1.0.0",
            ["ggml/cuda13/libGgmlOps.so", "ggml/cuda13/libcudart.so.13"]), Commit);
        Assert.Equal(2, inventory.NativeAssets.Count);
        Assert.All(inventory.NativeAssets, asset =>
        {
            Assert.Equal("linux-arm64", asset.Rid);
            Assert.Equal("NativeLibrary", asset.Kind);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("fixture bytes"))), asset.Sha256);
            Assert.Equal("NotRun", asset.RuntimeQualification);
        });
        Assert.True(inventory.HasTransitiveGgmlNatives);
        Assert.Equal("AssetsPresent", inventory.Coverage.Single(item => item.Family == "Ggml" && item.Rid == "linux-arm64").State);
        Assert.Equal(25, inventory.Coverage.Count);
        Assert.All(inventory.Coverage, coverage => Assert.Equal("NotRun", coverage.RuntimeQualification));
    }

    [Fact]
    public void AnyCpuNativeAssetIsNotClassifiedAsManagedOnly()
    {
        RuntimeInventory inventory = RuntimeInventory.Read(Fixture("Magick.NET-Q8-AnyCPU", "14.16.0", ["runtimes/linux-arm64/native/Magick.Native-Q8-arm64.dll.so"]), Commit);
        Assert.Single(inventory.NativeAssets);
        Assert.Equal("AssetsPresent", inventory.Coverage.Single(item => item.Family == "Magick" && item.Rid == "linux-arm64").State);
    }

    [Fact]
    public void ActualPeHeadersExposeMislabelledRidWithoutClaimingNativeExecution()
    {
        string assets = Fixture("Vendor", "1.0.0", ["runtimes/win-arm64/native/vendor.dll"]);
        string payload = Path.Combine(Path.GetDirectoryName(assets)!, "packages", "vendor", "1.0.0", "runtimes", "win-arm64", "native", "vendor.dll");
        File.Copy(typeof(RuntimeInventory).Assembly.Location, payload, overwrite: true);
        RuntimeAsset asset = Assert.Single(RuntimeInventory.Read(assets, Commit).NativeAssets);
        Assert.Equal("x86", asset.HeaderArchitecture);
        Assert.Equal("HeaderMismatch", asset.ArchitectureValidation);
        Assert.Equal("NotRun", asset.RuntimeQualification);
    }

    [Fact]
    public void MissingDeclaredFileIsNotInventoriedAsPresent()
    {
        string assets = Fixture("Whisper.net.Runtime", "1.9.1", ["build/linux-x64/libwhisper.so"], createPayload: false);
        Assert.ThrowsAny<IOException>(() => RuntimeInventory.Read(assets, Commit));
    }

    [Fact]
    public void ManifestVersionMustMatchRestoreGraph()
    {
        string assets = Fixture("Whisper.net.Runtime", "1.9.1", [], manifestVersion: "1.9.0");
        Assert.Throws<InvalidDataException>(() => RuntimeInventory.Read(assets, Commit));
    }

    [Theory]
    [InlineData("../outside.so")]
    [InlineData("runtimes/linux-x64/native/../../../../outside.so")]
    [InlineData("runtimes\\linux-x64\\native\\outside.so")]
    public void PackagePathsMustRemainPortableAndBounded(string path)
    {
        string assets = Fixture("Whisper.net.Runtime", "1.9.1", [path], createPayload: false);
        // A manifest itself must also be resolved through the same bounded path validator.
        JsonObject root = JsonNode.Parse(File.ReadAllText(assets))!.AsObject();
        root["libraries"]!["Whisper.net.Runtime/1.9.1"]!["path"] = path;
        File.WriteAllText(assets, root.ToJsonString());
        Assert.Throws<InvalidDataException>(() => RuntimeInventory.Read(assets, Commit));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void SourceProvenanceRequiresTheFullGitIdentity(string commit) =>
        Assert.Throws<ArgumentException>(() => RuntimeInventory.Read("not-opened.json", commit));

    private static string Fixture(string id, string version, string[] files, bool createPayload = true, string? manifestVersion = null)
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Directory.Build.props"))) repository = repository.Parent;
        string directory = Path.Combine(repository!.FullName, "tmp", "runtime-inventory-tests", Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(directory, "packages");
        string package = id.ToLowerInvariant() + "/" + version;
        string packageDirectory = Path.Combine(cache, package);
        Directory.CreateDirectory(packageDirectory);
        const string manifest = "fixture.nuspec";
        File.WriteAllText(Path.Combine(packageDirectory, manifest), $"<package><metadata><id>{id}</id><version>{manifestVersion ?? version}</version></metadata></package>");
        if (createPayload)
            foreach (string file in files)
            {
                string path = Path.Combine(packageDirectory, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "fixture bytes");
            }
        var fileArray = new JsonArray(files.Select(file => JsonValue.Create(file)).ToArray());
        fileArray.Add(manifest);
        var root = new JsonObject
        {
            ["packageFolders"] = new JsonObject { [cache] = new JsonObject() },
            ["targets"] = new JsonObject { ["net11.0"] = new JsonObject { [id + "/" + version] = new JsonObject() } },
            ["libraries"] = new JsonObject
            {
                [id + "/" + version] = new JsonObject { ["type"] = "package", ["path"] = package, ["files"] = fileArray }
            }
        };
        string assets = Path.Combine(directory, "project.assets.json");
        File.WriteAllText(assets, root.ToJsonString());
        return assets;
    }
}
