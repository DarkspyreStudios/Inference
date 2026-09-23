using System.Text;
using System.Net;
using System.Security.Cryptography;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Persistence;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenCompatibilityTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("multiple-layers")]
    [InlineData("architecture")]
    [InlineData("template")]
    [InlineData("context")]
    [InlineData("tensor")]
    [InlineData("shape")]
    [InlineData("quantization")]
    [InlineData("shards")]
    public void CheckpointPolicyValidatesActualGgufMetadata(string change)
    {
        string directory = Directory.CreateTempSubdirectory("qwen-metadata-").FullName;
        string path = Path.Combine(directory, TensorLlmModels.Qwen3FourBillionQ4Km.Artifacts[0].RelativePath);
        try
        {
            File.WriteAllBytes(path, Fixture(change));
            using var file = GgufFile.OpenWithoutSiblingShards(path);
            if (change is "valid" or "multiple-layers") { QwenModelCompatibility.ValidateCheckpoint(file, TensorLlmModels.Qwen3FourBillionQ4Km); }
            else { Assert.Throws<InvalidDataException>(() => QwenModelCompatibility.ValidateCheckpoint(file, TensorLlmModels.Qwen3FourBillionQ4Km)); }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task QwenPassesEveryNamedArtifactFromThePackLeaseToTheForkLoader()
    {
        byte[][] shards = [Fixture("valid", 0), Fixture("valid", 1)];
        TensorModelPackDefinition definition = TensorLlmModels.Qwen3FourBillionQ4Km with
        {
            Artifacts = shards.Select((bytes, index) => new TensorModelArtifactDefinition
            {
                Role = index == 0 ? "model" : "model-shard-2",
                RelativePath = $"quant/model-{index + 1:D5}-of-00002.gguf",
                Length = bytes.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                Sources = [new Uri($"https://example.test/{index}")]
            }).ToArray()
        };
        using var http = new HttpClient(new ShardHandler(shards));
        using var packs = new TensorModelPackManager(new TensorModelCatalog([definition]), new MemoryStore(), http);
        await packs.InstallAsync(definition.Key);
        await using TensorModelPackLease lease = await packs.AcquireAsync(definition.Key);
        PersistenceFileSet files = QwenModelRuntime.CreateFileSet(definition, lease);
        Assert.Equal(definition.Artifacts[0].RelativePath, files.PrimaryPath);
        Assert.Equal(2, files.Files.Count);
        using GgufFile file = await GgufFile.OpenAsync(files);
        Assert.Equal(13, file.Tensors.Count);
        QwenModelCompatibility.ValidateCheckpoint(file, definition);
        file.ThrowIfTruncated();
        string[] paths = file.FilePaths.ToArray();
        file.Dispose();
        Assert.All(paths, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public void PackPolicyRejectsNonQwenFamiliesAndMissingModelRole()
    {
        Assert.Throws<ArgumentException>(() => QwenModelCompatibility.Validate(TensorLlmModels.Qwen3FourBillionQ4Km with { RuntimeArchitecture = "gemma4" }));
        Assert.Throws<ArgumentException>(() => QwenModelCompatibility.Validate(TensorLlmModels.Qwen3FourBillionQ4Km with
        { Artifacts = [TensorLlmModels.Qwen3FourBillionQ4Km.Artifacts[0] with { Role = "unknown" }] }));
        Assert.Throws<ArgumentException>(() => QwenModelCompatibility.Validate(TensorLlmModels.Qwen3FourBillionQ4Km with
        { SupportedBackends = new HashSet<TensorBackendKind> { TensorBackendKind.Auto } }));
    }

    private static byte[] Fixture(string change, int? shard = null)
    {
        var tensors = new List<(string Name, ulong[] Shape)>
        {
            ("token_embd.weight", [4, 2]), ("output_norm.weight", [4]),
            ("blk.0.attn_norm.weight", [4]), ("blk.0.attn_q.weight", [4, 4]),
            ("blk.0.attn_k.weight", [4, 2]), ("blk.0.attn_v.weight", [4, 2]),
            ("blk.0.attn_q_norm.weight", [2]), ("blk.0.attn_k_norm.weight", [2]),
            ("blk.0.attn_output.weight", [4, 4]), ("blk.0.ffn_norm.weight", [4]),
            ("blk.0.ffn_gate.weight", [4, 8]), ("blk.0.ffn_up.weight", [4, 8]), ("blk.0.ffn_down.weight", [8, 4])
        };
        if (change == "tensor") { tensors.RemoveAt(2); }
        if (change == "shape") { tensors[2] = (tensors[2].Name, [5]); }
        if (change == "multiple-layers")
        {
            var firstLayer = tensors.Skip(2).ToArray();
            for (int layer = 1; layer < 4; layer++)
            {
                tensors.AddRange(firstLayer.Select(tensor => (tensor.Name.Replace("blk.0.", $"blk.{layer}.", StringComparison.Ordinal), tensor.Shape)));
            }
        }
        if (shard == 0) { tensors = tensors.Take(6).ToList(); }
        if (shard == 1) { tensors = tensors.Skip(6).ToList(); }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x46554747u); writer.Write(3u); writer.Write((ulong)tensors.Count); writer.Write(shard.HasValue ? 13ul : 12ul);
        Text(writer, "general.architecture", change == "architecture" ? "gemma4" : "qwen3");
        Number(writer, "qwen3.block_count", change == "multiple-layers" ? 4u : 1u); Number(writer, "qwen3.embedding_length", 4);
        Number(writer, "qwen3.attention.head_count", 2); Number(writer, "qwen3.attention.head_count_kv", 1);
        Number(writer, "qwen3.feed_forward_length", 8); Number(writer, "qwen3.context_length", change == "context" ? 0u : 2048u);
        Text(writer, "tokenizer.ggml.model", "gpt2"); Text(writer, "tokenizer.chat_template", change == "template" ? "" : "template");
        Strings(writer, "tokenizer.ggml.tokens", ["a", "b"]); Strings(writer, "tokenizer.ggml.merges", ["a b"]);
        Number(writer, "split.count", change == "shards" || shard.HasValue ? 2u : 1u);
        if (shard.HasValue) { Number(writer, "split.no", (uint)shard.Value); }
        ulong offset = 0;
        foreach ((string name, ulong[] shape) in tensors)
        {
            String(writer, name); writer.Write((uint)shape.Length);
            foreach (ulong dimension in shape) { writer.Write(dimension); }
            writer.Write(change == "quantization" ? 999u : 0u); writer.Write(offset);
            offset += shape.Aggregate(4ul, (size, dimension) => size * dimension);
        }
        while (stream.Position % 32 != 0) { writer.Write((byte)0); }
        writer.Write(new byte[checked((int)offset)]);
        return stream.ToArray();
    }
    private static void Number(BinaryWriter writer, string key, uint value) { String(writer, key); writer.Write(4u); writer.Write(value); }
    private static void Text(BinaryWriter writer, string key, string value) { String(writer, key); writer.Write(8u); String(writer, value); }
    private static void Strings(BinaryWriter writer, string key, string[] values)
    { String(writer, key); writer.Write(9u); writer.Write(8u); writer.Write((ulong)values.Length); foreach (string value in values) { String(writer, value); } }
    private static void String(BinaryWriter writer, string value) { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes); }

    private sealed class ShardHandler(byte[][] shards) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(shards[int.Parse(request.RequestUri!.AbsolutePath[1..], System.Globalization.CultureInfo.InvariantCulture)]) });
    }
    private sealed class MemoryStore : IPersistenceStore
    {
        private readonly Dictionary<(string Store, string Asset), byte[]> _assets = [];
        public Task<bool> ExistsAsync(string storeName, string assetName, CancellationToken cancellationToken = default) => Task.FromResult(_assets.ContainsKey((storeName, assetName)));
        public Task<Stream?> OpenReadAsync(string storeName, string assetName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(_assets.TryGetValue((storeName, assetName), out byte[]? bytes) ? new MemoryStream(bytes, writable: false) : null);
        public async Task SaveAsync(string storeName, string assetName, Stream content, CancellationToken cancellationToken = default)
        { using var buffer = new MemoryStream(); await content.CopyToAsync(buffer, cancellationToken); _assets[(storeName, assetName)] = buffer.ToArray(); }
        public Task DeleteAsync(string storeName, string assetName, CancellationToken cancellationToken = default)
        { _assets.Remove((storeName, assetName)); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> EnumerateAsync(string storeName, string assetNamePrefix, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_assets.Keys.Where(key => key.Store == storeName && key.Asset.StartsWith(assetNamePrefix, StringComparison.Ordinal)).Select(key => key.Asset).ToArray());
    }
}
