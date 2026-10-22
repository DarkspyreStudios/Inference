using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Tensor.Common;
using Darkspyre.Inference.Tensor.Embedding.Gemma;

namespace Darkspyre.Inference.Tensor.Embedding.Tests;

/// <summary>
/// One request of mixed-length texts returns the vectors each text gets alone. The engine runs on CPU through the shipped
/// pack manager, loader and ONNX path, with a small real graph in place of EmbeddingGemma: its sentence_embedding is the
/// attention_mask-weighted mean of token and position embeddings. Left padding, a mask that covers padding, or rows returned
/// out of order change that mean. The test covers the request's batching, right padding, mask, row order and
/// normalization. It does not cover EmbeddingGemma's own attention; the real-model parity is the DSA-1204 probe measurement.
/// The same graph runs a many-run document request beside a query, to show that the query takes the next run.
/// </summary>
public sealed class GemmaBatchingTests : IDisposable
{
    private const int Positions = 512;
    private const int Vocabulary = 260; // <pad>, <eos>, <bos>, <unk> and the 256 byte-fallback tokens.
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gemma-batching-" + Guid.NewGuid().ToString("N"));
    private static readonly TensorModelKey Key = GemmaEmbeddingModels.EmbeddingGemma2.Key;

    [Fact]
    public async Task MixedLengthInputsGetTheVectorsTheyGetAlone()
    {
        using TensorModelPackManager packs = await InstalledAsync();
        await using var engine = new GemmaEmbeddingInference(packs, TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(Key);
        // About 320, 30, 460 and 100 tokens: the first three shortest share one run of at most 1,024 padded tokens, the
        // longest runs alone.
        string[] texts = [new string('m', 300), "a", new string('l', 440), new string('s', 80)];

        EmbeddingResult together = await engine.EmbedAsync(new() { ModelId = Key.ModelId, Inputs = [.. texts.Select(Text)] });

        Assert.Equal(texts.Length, together.Vectors.Count);
        for (int index = 0; index < texts.Length; index++)
        {
            EmbeddingResult alone = await engine.EmbedAsync(new() { ModelId = Key.ModelId, Inputs = [Text(texts[index])] });
            Assert.Equal(alone.Vectors[0], together.Vectors[index], (expected, actual) => Math.Abs(expected - actual) < 1e-5f);
        }
        Assert.NotEqual(together.Vectors[0], together.Vectors[1], (left, right) => Math.Abs(left - right) < 1e-5f);

        static EmbeddingInput Text(string text) => new TextEmbeddingInput(text);
    }

    [Fact]
    public async Task QueryRunsBeforeTheRemainingRunsOfADocumentRequest()
    {
        using TensorModelPackManager packs = await InstalledAsync();
        await using var engine = new GemmaEmbeddingInference(packs, TensorBackendKind.Cpu);
        await engine.LoadAndWaitAsync(Key);
        // About 480 tokens each: two inputs fill one run, so the request is 1,000 runs.
        EmbeddingInput[] chunks = [.. Enumerable.Range(0, 2000).Select(index => new TextEmbeddingInput(index + new string('d', 450)))];

        Task<EmbeddingResult> document = engine.EmbedAsync(new() { ModelId = Key.ModelId, Inputs = chunks });
        EmbeddingResult query = await engine.EmbedAsync(new() { ModelId = Key.ModelId, Inputs = [new TextEmbeddingInput("needle")], Kind = EmbeddingInputKind.Query });

        Assert.False(document.IsCompleted, "The query ran after every run of the document request.");
        Assert.Single(query.Vectors);
        Assert.Equal(chunks.Length, (await document).Vectors.Count);
    }

    private async Task<TensorModelPackManager> InstalledAsync()
    {
        Directory.CreateDirectory(_root);
        byte[] graph = Graph();
        var artifacts = new List<TensorModelArtifactDefinition>();
        foreach (var artifact in GemmaEmbeddingModels.EmbeddingGemma2.Artifacts)
        {
            // The vision session loads the same graph; no input here is an image. The external-data files are unread.
            byte[] bytes = artifact.Role switch
            {
                "model" or "vision" => graph,
                "tokenizer" => Tokenizer(),
                _ => [0],
            };
            string path = Path.Combine(_root, artifact.RelativePath);
            await File.WriteAllBytesAsync(path, bytes);
            artifacts.Add(artifact with { Length = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Sources = [new Uri(path)] });
        }
        var packs = new TensorModelPackManager(new TensorModelCatalog([GemmaEmbeddingModels.EmbeddingGemma2 with { Artifacts = artifacts }]), Path.Combine(_root, "packs"));
        await packs.InstallAsync(Key);
        return packs;
    }

    // A byte-fallback BPE vocabulary with no merges: every text encodes as its UTF-8 bytes between <bos> and <eos>.
    private static byte[] Tokenizer()
    {
        var vocabulary = new Dictionary<string, int> { ["<pad>"] = 0, ["<eos>"] = 1, ["<bos>"] = 2, ["<unk>"] = 3 };
        for (int value = 0; value < 256; value++) vocabulary[$"<0x{value:X2}>"] = 4 + value;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = new { type = "BPE", byte_fallback = true, vocab = vocabulary, merges = Array.Empty<string[]>() },
            added_tokens = Enumerable.Range(0, 4).Select(id => new { id }),
        });
    }

    // The ONNX graph (IR 8, opset 17) with EmbeddingGemma's inputs and output:
    // sentence_embedding = sum(mask * (table[input_ids] + positions[0..S))) / sum(mask) over the sequence.
    private static byte[] Graph()
    {
        float[] table = new float[Vocabulary * 768], positions = new float[Positions * 768];
        for (int index = 0; index < table.Length; index++) table[index] = MathF.Sin(index * 0.37f);
        for (int index = 0; index < positions.Length; index++) positions[index] = 0.5f * MathF.Cos(index * 0.13f);
        byte[] graph =
            [.. Node("Shape", ["input_ids"], "shape"),
            .. Node("Gather", ["shape", "one"], "length"),
            .. Node("Range", ["zero", "length", "one"], "steps"),
            .. Node("Gather", ["positions", "steps"], "position"),
            .. Node("Gather", ["table", "input_ids"], "token"),
            .. Node("Add", ["token", "position"], "hidden"),
            .. Node("Cast", ["attention_mask"], "weights", Attribute("to", 1)),
            .. Node("Unsqueeze", ["weights", "last"], "weights3"),
            .. Node("Mul", ["hidden", "weights3"], "kept"),
            .. Node("ReduceSum", ["kept", "sequence"], "total", Attribute("keepdims", 0)),
            .. Node("ReduceSum", ["weights3", "sequence"], "count", Attribute("keepdims", 0)),
            .. Node("Div", ["total", "count"], "sentence_embedding"),
            .. Onnx.Text(2, "batching"),
            .. Initializer("table", 1, [Vocabulary, 768], Floats(table)),
            .. Initializer("positions", 1, [Positions, 768], Floats(positions)),
            .. Initializer("zero", 7, [], BitConverter.GetBytes(0L)),
            .. Initializer("one", 7, [], BitConverter.GetBytes(1L)),
            .. Initializer("last", 7, [1], BitConverter.GetBytes(2L)),
            .. Initializer("sequence", 7, [1], BitConverter.GetBytes(1L)),
            .. Value(11, "input_ids", 7, "batch_size", "sequence_length"),
            .. Value(11, "attention_mask", 7, "batch_size", "sequence_length"),
            .. Value(11, "image_features", 1, "num_image_tokens", 512),
            .. Value(11, "video_features", 1, "num_video_tokens", 512),
            .. Value(11, "audio_features", 1, "num_audio_tokens", 512),
            .. Value(12, "sentence_embedding", 1, "batch_size", 768)];
        return [.. Onnx.Varint(1, 8), .. Onnx.Field(8, [.. Onnx.Text(1, ""), .. Onnx.Varint(2, 17)]), .. Onnx.Field(7, graph)];

        // An INT attribute (AttributeProto type 2).
        static byte[] Attribute(string name, long value) => Onnx.Field(5, [.. Onnx.Text(1, name), .. Onnx.Varint(3, value), .. Onnx.Varint(20, 2)]);
        static byte[] Node(string operation, string[] inputs, string output, byte[]? attribute = null) => Onnx.Field(1,
            [.. inputs.SelectMany(input => Onnx.Text(1, input)), .. Onnx.Text(2, output), .. Onnx.Text(4, operation), .. attribute ?? []]);
        static byte[] Initializer(string name, int type, long[] dimensions, byte[] data) => Onnx.Field(5,
            [.. dimensions.SelectMany(dimension => Onnx.Varint(1, dimension)), .. Onnx.Varint(2, type), .. Onnx.Text(8, name), .. Onnx.Field(9, data)]);
        static byte[] Value(int field, string name, int type, object first, object second) => Onnx.Field(field,
            [.. Onnx.Text(1, name), .. Onnx.Field(2, Onnx.Field(1,
                [.. Onnx.Varint(1, type), .. Onnx.Field(2, [.. Dimension(first), .. Dimension(second)])]))]);
        static byte[] Dimension(object size) => Onnx.Field(1, size is string name ? Onnx.Text(2, name) : Onnx.Varint(1, (int)size));
        static byte[] Floats(float[] values) => [.. values.SelectMany(BitConverter.GetBytes)];
    }

    // Protocol-buffer encoding of the ONNX message fields the graph uses: a field is its key, then a varint or a
    // length-prefixed value.
    private static class Onnx
    {

        internal static byte[] Varint(int field, long value) => [.. Key(field, 0), .. Unsigned((ulong)value)];

        internal static byte[] Text(int field, string value) => Field(field, Encoding.UTF8.GetBytes(value));

        internal static byte[] Field(int field, byte[] value) => [.. Key(field, 2), .. Unsigned((ulong)value.Length), .. value];

        private static byte[] Key(int field, int wireType) => Unsigned((ulong)(field << 3 | wireType));

        private static byte[] Unsigned(ulong value)
        {
            var bytes = new List<byte>();
            do
            {
                byte next = (byte)(value & 0x7f);
                value >>= 7;
                bytes.Add(value == 0 ? next : (byte)(next | 0x80));
            }
            while (value != 0);
            return [.. bytes];
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
