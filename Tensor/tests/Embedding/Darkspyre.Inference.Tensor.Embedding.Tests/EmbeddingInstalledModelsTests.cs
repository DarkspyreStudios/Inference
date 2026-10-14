using System.Security.Cryptography;
using System.Text;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Ggml;
using Darkspyre.Persist;

namespace Darkspyre.Inference.Tensor.Embedding.Tests;

public sealed class EmbeddingInstalledModelsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darkspyre-embedding-installed-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public async Task EmbeddingReportsAQwen3EmbeddingHeaderAndSkipsChatShapedHeaders()
    {
        var provider = new EmbeddingEngineProvider(GgmlRuntimeOwner.GetOrCreate());
        // Qwen3-Embedding-0.6B's header keys, as measured on its published GGUF.
        TensorModelPackDefinition embedding = await InstallAsync(provider, "embedding.gguf", pooling: 3, dimensions: 1024);
        await InstallAsync(provider, "chat.gguf", pooling: null, dimensions: 1024);
        await InstallAsync(provider, "wide.gguf", pooling: 3, dimensions: 2560);

        IReadOnlyList<TensorModelPackDefinition> reported = await provider.GetInstalledModelsAsync(new OsUserPersist(_root));

        Assert.Equal(embedding.Key, Assert.Single(reported).Key);
    }

    private async Task<TensorModelPackDefinition> InstallAsync(EmbeddingEngineProvider provider, string name, uint? pooling, uint dimensions)
    {
        Directory.CreateDirectory(_root);
        byte[] bytes = Header(pooling, dimensions);
        string source = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(source, bytes);
        var definition = new TensorModelPackDefinition
        {
            Key = new("imported/" + name, "test"),
            Capability = TensorCapability.Embedding,
            RuntimeArchitecture = TensorEmbeddingModels.Qwen3Gguf.Name,
            License = "Apache-2.0",
            SupportedBackends = TensorEmbeddingModels.Backends,
            Artifacts = [new() { Role = "model", RelativePath = name, Length = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Sources = [new(new Uri(source).AbsoluteUri)] }]
        };
        using var packs = new TensorModelPackManager(new([definition]), Path.Combine(_root, provider.Definition.ModelStoreKey));
        await packs.InstallAsync(definition.Key);
        return definition;
    }

    // A GGUF v3 file with the given Qwen3 keys and no tensors, padded past the header.
    private static byte[] Header(uint? pooling, uint dimensions)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            var values = new List<(string Key, Action Write)>
            {
                ("general.architecture", () => String(writer, "qwen3")),
                ("qwen3.embedding_length", () => UInt32(writer, dimensions)),
                ("qwen3.context_length", () => UInt32(writer, 32768)),
                ("tokenizer.ggml.model", () => String(writer, "gpt2")),
                ("tokenizer.ggml.tokens", () =>
                {
                    string[] tokens = ["hello", "<|endoftext|>"];
                    writer.Write(9u);
                    writer.Write(8u);
                    writer.Write((ulong)tokens.Length);
                    foreach (string token in tokens) { Text(writer, token); }
                })
            };
            if (pooling is uint value) { values.Add(("qwen3.pooling_type", () => UInt32(writer, value))); }
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
