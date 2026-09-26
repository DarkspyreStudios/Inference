using System.Text;
using System.Net;
using System.Security.Cryptography;
using Darkspyre.Inference.Tensor.Common;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.LLM.Qwen.Tests;

public sealed class QwenCompatibilityTests
{
    private static readonly string[] ExplicitLayout = ["linear_attention", "full_attention", "linear_attention"];
    private static readonly string[] MismatchedLayout = ["full_attention"];

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

    [Theory]
    [InlineData("qwen35", "valid")]
    [InlineData("qwen35moe", "valid")]
    [InlineData("qwen3next", "valid")]
    [InlineData("qwen35", "state")]
    [InlineData("qwen35", "layers")]
    [InlineData("qwen4exp", "valid")]
    public void HybridCheckpointPolicyValidatesTheRecurrentStateAndLayout(string architecture, string change)
    {
        string directory = Directory.CreateTempSubdirectory("qwen35-metadata-").FullName;
        string path = Path.Combine(directory, TensorLlmModels.Qwen3FourBillionQ4Km.Artifacts[0].RelativePath);
        try
        {
            File.WriteAllBytes(path, HybridFixture(architecture, change));
            using var file = GgufFile.OpenWithoutSiblingShards(path);
            if (change == "valid" && architecture != "qwen4exp") { QwenModelCompatibility.ValidateCheckpoint(file, TensorLlmModels.Qwen3FourBillionQ4Km); }
            else { Assert.Throws<InvalidDataException>(() => QwenModelCompatibility.ValidateCheckpoint(file, TensorLlmModels.Qwen3FourBillionQ4Km)); }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void FullAttentionLayersFollowTheLayerListOrTheInterval()
    {
        Assert.Equal(16, QwenModelCompatibility.FullAttentionLayers(new Dictionary<string, object>(), "qwen35", 64));
        Assert.Equal(8, QwenModelCompatibility.FullAttentionLayers(
            new Dictionary<string, object> { ["qwen35.full_attention_interval"] = 8u }, "qwen35", 64));
        Assert.Equal(1, QwenModelCompatibility.FullAttentionLayers(
            new Dictionary<string, object> { ["qwen35.layer_types"] = ExplicitLayout }, "qwen35", 3));
        // A list that does not match the block count is ignored, as TensorSharp's loader ignores it.
        Assert.Equal(1, QwenModelCompatibility.FullAttentionLayers(
            new Dictionary<string, object> { ["qwen35.layer_types"] = MismatchedLayout }, "qwen35", 4));
        Assert.Equal(36, QwenModelCompatibility.FullAttentionLayers(new Dictionary<string, object>(), "qwen3", 36));
    }

    [Fact]
    public async Task AnInstalledShardedPackOpensFromItsPrimaryPathWithItsSiblingShards()
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
        string directory = Directory.CreateTempSubdirectory("qwen-shards-").FullName;
        try
        {
            using var http = new HttpClient(new ShardHandler(shards));
            using var packs = new TensorModelPackManager(new TensorModelCatalog([definition]), directory, http);
            await packs.InstallAsync(definition.Key);
            await using TensorModelPackLease lease = await packs.AcquireAsync(definition.Key);
            using var file = new GgufFile(lease.GetRequiredArtifact("model"));
            Assert.Equal(13, file.Tensors.Count);
            QwenModelCompatibility.ValidateCheckpoint(file, definition);
            file.ThrowIfTruncated();
            Assert.Equal(
                [lease.GetRequiredArtifact("model"), lease.GetRequiredArtifact("model-shard-2")],
                file.FilePaths.Select(Path.GetFullPath).Order(StringComparer.Ordinal).ToArray());
        }
        finally { Directory.Delete(directory, true); }
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

    internal static byte[] Fixture(string change, int? shard = null)
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
    // Four layers, one full-attention layer every fourth, and four tensors per layer.
    internal static byte[] HybridFixture(string arch, string change)
    {
        var tensors = new List<(string Name, ulong[] Shape)> { ("token_embd.weight", [4, 2]), ("output_norm.weight", [4]) };
        int layers = change == "layers" ? 1 : 4;
        for (int layer = 0; layer < layers; layer++)
        {
            foreach (string name in new[] { "attn_norm.weight", "ffn_norm.weight", "ffn_up.weight", "ffn_down.weight" })
            { tensors.Add(($"blk.{layer}.{name}", [4])); }
        }
        string[] state = ["ssm.inner_size", "ssm.state_size", "ssm.group_count", "ssm.time_step_rank", "ssm.conv_kernel"];
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x46554747u); writer.Write(3u); writer.Write((ulong)tensors.Count);
        writer.Write((ulong)(12 + (change == "state" ? state.Length - 1 : state.Length)));
        Text(writer, "general.architecture", arch);
        Number(writer, $"{arch}.block_count", 4); Number(writer, $"{arch}.embedding_length", 4);
        Number(writer, $"{arch}.attention.head_count", 2); Number(writer, $"{arch}.attention.head_count_kv", 1);
        Number(writer, $"{arch}.context_length", 262144); Number(writer, $"{arch}.full_attention_interval", 4);
        foreach (string key in change == "state" ? state.Skip(1) : state) { Number(writer, $"{arch}.{key}", 2); }
        Text(writer, "tokenizer.ggml.model", "gpt2"); Text(writer, "tokenizer.chat_template", "template");
        Strings(writer, "tokenizer.ggml.tokens", ["a", "b"]); Strings(writer, "tokenizer.ggml.merges", ["a b"]);
        Number(writer, "split.count", 1u);
        ulong offset = 0;
        foreach ((string name, ulong[] shape) in tensors)
        {
            String(writer, name); writer.Write((uint)shape.Length);
            foreach (ulong dimension in shape) { writer.Write(dimension); }
            writer.Write(0u); writer.Write(offset);
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
}
