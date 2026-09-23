using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Decision.Laya.Tests;

public sealed class LayaModelResolverTests : IDisposable
{
    private static readonly string[] Roles = ["agent-config", "model", "tokenizer"];
    private static readonly int[] Temperatures = [1, 1, 1];
    private readonly string _root = Directory.CreateTempSubdirectory("laya-resolution-tests-").FullName;

    [Theory]
    [InlineData("laya-fixture.onnx", true)]
    [InlineData("laya-fixture-no-metadata.onnx", false)]
    [InlineData("laya-fixture-dynamic.onnx", false)]
    public void OnnxInspectionUsesMetadataWithoutCreatingAnInferenceSession(string fixture, bool valid)
    {
        using Stream content = File.OpenRead(Fixture(fixture));
        if (valid) LayaOnnxMetadata.Validate(content, 16);
        else Assert.Throws<InvalidDataException>(() => LayaOnnxMetadata.Validate(content, 16));
    }

    [Fact]
    public async Task ThreeArtifactFolderImportsAndCanBeResolvedAgainWithoutTokenizerConfig()
    {
        await PrepareAsync();
        var resolver = new LayaModelResolver();
        var selection = new TensorModelSelection(new TensorModelLocation.LocalFolder(_root, "laya.onnx"), License: "test-fixture");
        TensorModelResolution before = await resolver.ResolveAsync(selection);
        Assert.Equal(TensorCompatibilityState.Unverified, before.Evaluation.State);
        using var manager = new TensorModelPackManager(new([]), new TensorDirectFilePersistStore(Path.Combine(_root, "store")));
        TensorModelImportResult imported = await manager.ImportAsync(new(selection, resolver));
        Assert.Equal(TensorCompatibilityState.Supported, imported.Resolution.Evaluation.State);
        TensorModelPackDefinition definition = Assert.IsType<TensorModelPackDefinition>(imported.Resolution.Definition);
        Assert.Equal(Roles, definition.Artifacts.Select(item => item.Role).Order());
        Assert.Equal(TensorModelPackState.Installed, (await manager.VerifyAsync(definition.Key)).State);
        TensorModelArtifactDefinition primary = definition.Artifacts.Single(item => item.Role == "model");
        var companions = definition.Artifacts.Where(item => item.Role != "model").Select(item => new TensorArtifactLocation(item.Role,
            new TensorModelLocation.LocalFile(item.Sources[0].LocalPath), item.Provenance!.License, item.Length, item.Sha256)).ToArray();
        TensorModelResolution inspected = await resolver.ResolveAsync(new(new TensorModelLocation.LocalFile(Path.Combine(_root, "laya.onnx")),
            License: "test-fixture", ExpectedLength: primary.Length, ExpectedSha256: primary.Sha256, Companions: companions));
        Assert.Equal(TensorCompatibilityState.Supported, inspected.Evaluation.State);
        Assert.Equal(3, inspected.Definition!.Artifacts.Count);
    }

    [Fact]
    public async Task MissingCompanionAndMismatchedConfigurationNeverInstall()
    {
        await PrepareAsync();
        var resolver = new LayaModelResolver();
        var selection = new TensorModelSelection(new TensorModelLocation.LocalFolder(_root, "laya.onnx"), License: "test");
        File.Delete(Path.Combine(_root, "tokenizer.json"));
        Assert.Contains((await resolver.DiscoverAsync(selection)).Issues!, issue => issue.Code == "companion-missing");
        File.Copy(Fixture("tokenizer.json"), Path.Combine(_root, "tokenizer.json"));
        await File.WriteAllTextAsync(Path.Combine(_root, "rl_agent_config.json"), Configuration(64));
        using var manager = new TensorModelPackManager(new([]), new TensorDirectFilePersistStore(Path.Combine(_root, "store")));
        TensorModelImportResult rejected = await manager.ImportAsync(new(selection, resolver));
        Assert.Equal(TensorCompatibilityState.Unsupported, rejected.Resolution.Evaluation.State);
        Assert.Empty(manager.Definitions);
    }

    [Fact]
    public void InvalidProtobufBoundsAreRejected()
    {
        using var content = new MemoryStream([0x3a, 0xff, 0xff, 0xff, 0xff, 0x7f]);
        Assert.Throws<InvalidDataException>(() => LayaOnnxMetadata.Validate(content, 32));
    }

    private async Task PrepareAsync()
    {
        byte[] model = await File.ReadAllBytesAsync(Fixture("laya-fixture.onnx"));
        // Keep the existing executable fixture graph; change only its two token dimensions and matching metadata
        // from 16 to 32 so it also satisfies the serving configuration's minimum head/framing length.
        int dimensions = 0;
        for (int index = 0; index <= model.Length - 4; index++)
            if (model.AsSpan(index, 4).SequenceEqual(new byte[] { 0x0a, 0x02, 0x08, 0x10 }))
            { model[index + 3] = 0x20; dimensions++; }
        Assert.Equal(2, dimensions);
        byte[] text = Encoding.UTF8.GetBytes("laya.max_len\u0012\u000216");
        int offset = model.AsSpan().IndexOf(text);
        Assert.True(offset >= 0);
        model[offset + text.Length - 2] = (byte)'3';
        model[offset + text.Length - 1] = (byte)'2';
        await File.WriteAllBytesAsync(Path.Combine(_root, "laya.onnx"), model);
        File.Copy(Fixture("tokenizer.json"), Path.Combine(_root, "tokenizer.json"));
        await File.WriteAllTextAsync(Path.Combine(_root, "rl_agent_config.json"), Configuration(32));
    }

    private static string Configuration(int length) => JsonSerializer.Serialize(new
    { max_len = length, head_max_len = 16, temperature = Temperatures, temperature_by_options = new Dictionary<string, double>() });
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    public void Dispose() => Directory.Delete(_root, true);
}
