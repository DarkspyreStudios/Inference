using System.Security.Cryptography;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;
using Darkspyre.Persist;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenInstalledModelsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darkspyre-qwen-installed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public async Task QwenReportsNoBuiltInAndSkipsAPackThatIsNotAGguf()
    {
        var supplier = new GgmlRuntimeShutdownTests.Supplier(null);
        await using var owner = new GgmlRuntimeOwner(new(supplier.Build, supplier.Rid, []), supplier);
        var provider = new QwenEngineProvider(owner);
        var modelStore = new OsUserPersist(_root);
        Assert.Empty(provider.BuiltInModels);
        Assert.Empty(await provider.GetInstalledModelsAsync(modelStore));

        Directory.CreateDirectory(_root);
        byte[] bytes = "not a GGUF checkpoint"u8.ToArray();
        string source = Path.Combine(_root, "source.gguf");
        await File.WriteAllBytesAsync(source, bytes);
        var definition = new TensorModelPackDefinition
        {
            Key = new("imported/source.gguf", "test"),
            Capability = TensorCapability.Llm,
            RuntimeArchitecture = TensorLlmModels.QwenRuntimeArchitecture,
            License = "Apache-2.0",
            SupportedBackends = TensorLlmModels.Backends,
            Artifacts = [new() { Role = "model", RelativePath = "source.gguf", Length = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Sources = [new(new Uri(source).AbsoluteUri)] }]
        };
        QwenModelCompatibility.Validate(definition);
        using (var packs = new TensorModelPackManager(new([definition]), Path.Combine(_root, provider.Definition.ModelStoreKey)))
        {
            await packs.InstallAsync(definition.Key);
            Assert.Equal(TensorModelPackState.Installed, (await packs.GetStatusAsync(definition.Key)).State);
        }

        Assert.Empty(await provider.GetInstalledModelsAsync(modelStore));
    }
}
