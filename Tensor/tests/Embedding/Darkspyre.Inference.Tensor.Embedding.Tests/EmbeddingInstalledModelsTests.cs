using System.Security.Cryptography;
using System.Text;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;

namespace Darkspyre.Inference.Tensor.Embedding.Tests;

public sealed class EmbeddingInstalledModelsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darkspyre-embedding-installed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public async Task EmbeddingRetiresAQwen3EmbeddingHeaderAndStillReportsABertHeader()
    {
        var provider = new EmbeddingEngineProvider(GgmlRuntimeOwner.GetOrCreate());
        TensorModelPackDefinition bert = await InstallAsync(provider, "bert.gguf", TensorEmbeddingModels.BertGguf.Name,
            Header("bert", pooling: 2, "bert"));
        // Qwen3-Embedding-0.6B's header keys, as measured on its published GGUF.
        TensorModelPackDefinition qwen3 = await InstallAsync(provider, "qwen3-embedding.gguf", "qwen3-embedding-gguf",
            Header("qwen3", pooling: 3, "gpt2"));
        // A Qwen3 chat model of the same GGUF architecture names no pooling; it is neither reported nor retired.
        await InstallAsync(provider, "qwen3-chat.gguf", "qwen3-embedding-gguf", Header("qwen3", pooling: null, "gpt2"));

        using var packs = new TensorModelPackManager(new([]), Path.Combine(_root, provider.Definition.ModelStoreKey));
        TensorInstalledReport report = await provider.GetInstalledModelsAsync(packs);

        Assert.Equal(bert.Key, Assert.Single(report.Installed).Key);
        TensorUnsupportedPack retired = Assert.Single(report.Unsupported);
        Assert.Equal((qwen3.Key, TensorEmbeddingModels.Qwen3EmbeddingRetiredReason), (retired.Definition.Key, retired.Reason));
    }

    private async Task<TensorModelPackDefinition> InstallAsync(EmbeddingEngineProvider provider, string name, string architecture, byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(source, bytes);
        var definition = new TensorModelPackDefinition
        {
            Key = new("imported/" + name, "test"),
            Capability = TensorCapability.Embedding,
            RuntimeArchitecture = architecture,
            License = "Apache-2.0",
            SupportedBackends = TensorEmbeddingModels.BertGguf.Backends,
            Artifacts = [new() { Role = "model", RelativePath = name, Length = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Sources = [new(new Uri(source).AbsoluteUri)] }]
        };
        using var packs = new TensorModelPackManager(new([definition]), Path.Combine(_root, provider.Definition.ModelStoreKey));
        await packs.InstallAsync(definition.Key);
        return definition;
    }

    // A GGUF v3 file with an embedding header of 1,024 dimensions and an 8,192-token context, no tensors, padded past
    // the header.
    private static byte[] Header(string architecture, uint? pooling, string tokenizer)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            var values = new List<(string Key, Action Write)>
            {
                ("general.architecture", () => String(writer, architecture)),
                ($"{architecture}.embedding_length", () => UInt32(writer, 1024)),
                ($"{architecture}.context_length", () => UInt32(writer, 32768)),
                ("tokenizer.ggml.model", () => String(writer, tokenizer))
            };
            if (pooling is uint value) { values.Add(($"{architecture}.pooling_type", () => UInt32(writer, value))); }
            writer.Write("GGUF"u8);
            writer.Write(3u);
            writer.Write(0UL);
            writer.Write((ulong)values.Count);
            foreach ((string key, Action write) in values)
            {
                Text(writer, key);
                write();
            }
            writer.Write(new byte[4096]);
        }
        return stream.ToArray();
    }

    private static void Text(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }

    private static void String(BinaryWriter writer, string value)
    {
        writer.Write(8u);
        Text(writer, value);
    }

    private static void UInt32(BinaryWriter writer, uint value)
    {
        writer.Write(4u);
        writer.Write(value);
    }
}
