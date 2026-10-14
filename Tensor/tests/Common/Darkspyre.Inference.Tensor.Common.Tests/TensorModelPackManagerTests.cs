using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class TensorModelPackManagerTests : IDisposable
{
    private readonly string _packDirectory = Path.Combine(
        Path.GetTempPath(),
        $"darkspyre-tensor-tests-{Guid.NewGuid():N}");

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
    public async Task LocalImportReopensAfterSettingsAndEngineWireSerialization()
    {
        string sourceDirectory = Path.Combine(_packDirectory, "source");
        Directory.CreateDirectory(sourceDirectory);
        string sourcePath = Path.Combine(sourceDirectory, "local embedding.gguf");
        byte[] weights = "local weights"u8.ToArray();
        await File.WriteAllBytesAsync(sourcePath, weights);
        string managedDirectory = Path.Combine(_packDirectory, "managed");
        TensorModelPackDefinition definition;
        using (var manager = new TensorModelPackManager(new([]), managedDirectory))
        {
            TensorModelImportResult imported = await manager.ImportAsync(new(
                new(new TensorModelLocation.LocalFile(sourcePath), TensorBackendKind.Cpu, "test-only"), new LocalResolver()));
            definition = Assert.IsType<TensorModelPackDefinition>(imported.Resolution.Definition);
            Assert.Equal(TensorModelPackState.Installed, (await manager.VerifyAsync(definition.Key)).State);
        }

        // The host stores string enums; engine initialization uses Tensor's generated metadata.
        var settingsJson = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        JsonElement saved = JsonSerializer.SerializeToElement(definition, settingsJson);
        TensorModelPackDefinition restored = saved.Deserialize<TensorModelPackDefinition>(settingsJson)!;
        TensorModelPackDefinition registered = new TensorModelCatalog([restored]).GetRequired(definition.Key);
        byte[] wire = JsonSerializer.SerializeToUtf8Bytes(registered, TensorJson.ModelPack);
        TensorModelPackDefinition remote = JsonSerializer.Deserialize(wire, TensorJson.ModelPack)!;
        var catalog = new TensorModelCatalog([remote]);
        Uri source = Assert.Single(Assert.Single(catalog.GetRequired(definition.Key).Artifacts).Sources);
        Assert.True(source.IsAbsoluteUri);
        Assert.True(source.IsFile);
        Assert.Equal(sourcePath, source.LocalPath);

        File.Delete(sourcePath);
        using var reopened = new TensorModelPackManager(catalog, managedDirectory);
        Assert.Equal(TensorModelPackState.Installed, (await reopened.VerifyAsync(definition.Key)).State);
        await using TensorModelPackLease lease = await reopened.AcquireAsync(definition.Key);
        Assert.Equal(weights, await File.ReadAllBytesAsync(lease.GetRequiredArtifact("model")));
    }

    [Fact]
    public async Task InstallWritesTheDescriptionAndRemovalDeletesIt()
    {
        Directory.CreateDirectory(_packDirectory);
        byte[] weights = "described weights"u8.ToArray();
        string sourcePath = Path.Combine(_packDirectory, "source.gguf");
        await File.WriteAllBytesAsync(sourcePath, weights);
        TensorModelPackDefinition definition = Definition(weights) with
        {
            Artifacts = [Definition(weights).Artifacts[0] with { Sources = [new Uri(sourcePath)] }]
        };
        string packs = Path.Combine(_packDirectory, "store");
        string description = Path.Combine(packs, TensorModelPackManager.DescriptionName(definition.Key));
        using var manager = new TensorModelPackManager(new([definition]), packs);

        await manager.InstallAsync(definition.Key);
        Assert.Equal(JsonSerializer.Serialize(definition, TensorJson.ModelPack), await File.ReadAllTextAsync(description));

        // A pack installed before descriptions existed gains one on its next install.
        File.Delete(description);
        await manager.InstallAsync(definition.Key);
        Assert.True(File.Exists(description));

        await manager.RemoveAsync(definition.Key);
        Assert.False(File.Exists(description));
        Assert.False(File.Exists(Path.Combine(packs, TensorModelPackManager.ManifestName(definition.Key))));
    }

    private sealed class LocalResolver : TensorModelResolver
    {
        public override IReadOnlyList<TensorArtifactRequirement> Requirements { get; } = [new("model", "*.gguf")];

        public override Task<TensorModelResolution> ResolveCandidateAsync(TensorModelCandidate candidate,
            TensorBackendKind backend, ITensorArtifactReader reader, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TensorModelResolution(new(TensorCompatibilityState.Supported, [], []),
                TensorResolvedPack.Create(candidate, TensorCapability.Embedding, "test", new HashSet<TensorBackendKind> { TensorBackendKind.Cpu },
                    candidate.Artifacts.Select(artifact => TensorResolvedPack.Artifact(artifact, "model", artifact.Path)).ToArray())));
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

}
