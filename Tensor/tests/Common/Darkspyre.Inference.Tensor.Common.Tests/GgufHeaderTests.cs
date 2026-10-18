using System.Collections;
using System.Text;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Common.Tests;

public sealed class GgufHeaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"darkspyre-gguf-header-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public void ReadsWhatUpstreamGgufFileReportsForTheSameFile()
    {
        var gguf = new Gguf();
        gguf.String("general.architecture", "test");
        gguf.UInt32("test.block_count", 2);
        gguf.Int32("test.signed", -3);
        gguf.Float32("test.scale", 0.5f);
        gguf.Bool("test.flag", true);
        gguf.UInt64("test.large", 1UL << 40);
        gguf.Strings("tokenizer.ggml.tokens", ["a", "b", "c"]);
        gguf.Int32s("tokenizer.ggml.token_type", [1, 2, 3]);
        gguf.Tensor("token_embd.weight", [4, 3], 0, 0);
        gguf.Tensor("output_norm.weight", [4], 0, 64);
        byte[] bytes = gguf.Build(dataBytes: 96);
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "model.gguf");
        File.WriteAllBytes(path, bytes);

        GgufHeader header = GgufHeader.Read(new MemoryStream(bytes));
        using var upstream = new GgufFile(path);

        Assert.Equal(upstream.Version, header.Version);
        Assert.Equal(upstream.DataOffset, header.DataOffset);
        Assert.Equal(bytes.Length, header.FileLength);
        Assert.Equal(upstream.Metadata.Keys.Order(StringComparer.Ordinal), header.Values.Keys.Order(StringComparer.Ordinal));
        foreach ((string key, object value) in upstream.Metadata)
        {
            object read = header.Values[key];
            Assert.Equal(value.GetType(), read.GetType());
            if (value is IEnumerable sequence and not string) Assert.Equal(sequence.Cast<object>(), ((IEnumerable)read).Cast<object>());
            else Assert.Equal(value, read);
        }
        Assert.Equal(upstream.Tensors.Keys.Order(StringComparer.Ordinal), header.Tensors.Keys.Order(StringComparer.Ordinal));
        foreach ((string name, GgufTensorInfo tensor) in upstream.Tensors)
        {
            GgufTensorInfo read = header.Tensors[name];
            Assert.Equal((tensor.Name, tensor.Type, tensor.Offset), (read.Name, read.Type, read.Offset));
            Assert.Equal(tensor.Shape, read.Shape);
        }
    }

    [Theory]
    [InlineData("keys")]
    [InlineData("tensors")]
    [InlineData("string")]
    [InlineData("array")]
    public void RefusesADeclaredCountTheStreamCannotHold(string field)
    {
        var gguf = new Gguf { KeyCountOverride = field == "keys" ? 1UL << 40 : null, TensorCountOverride = field == "tensors" ? 1UL << 40 : null };
        if (field == "string") gguf.Raw("general.name", writer => { writer.Write(8u); writer.Write(1UL << 40); });
        if (field == "array") gguf.Raw("tokenizer.ggml.scores", writer => { writer.Write(9u); writer.Write(6u); writer.Write(1UL << 40); });
        if (field is "keys" or "tensors") gguf.String("general.architecture", "test");

        Assert.Throws<InvalidDataException>(() => GgufHeader.Read(new MemoryStream(gguf.Build(dataBytes: 0))));
    }

    [Fact]
    public void RefusesADuplicateKey()
    {
        var gguf = new Gguf();
        gguf.String("general.architecture", "a");
        gguf.String("general.architecture", "b");
        Assert.Throws<InvalidDataException>(() => GgufHeader.Read(new MemoryStream(gguf.Build(dataBytes: 0))));
    }

    [Fact]
    public void RefusesADuplicateTensor()
    {
        var gguf = new Gguf();
        gguf.Tensor("w", [4], 0, 0);
        gguf.Tensor("w", [4], 0, 32);
        Assert.Throws<InvalidDataException>(() => GgufHeader.Read(new MemoryStream(gguf.Build(dataBytes: 64))));
    }

    [Fact]
    public void RefusesARankAboveFour()
    {
        var gguf = new Gguf();
        gguf.Tensor("w", [1, 1, 1, 1, 1], 0, 0);
        Assert.Throws<InvalidDataException>(() => GgufHeader.Read(new MemoryStream(gguf.Build(dataBytes: 32))));
    }

    [Theory]
    [InlineData(48u)]
    [InlineData(8192u)]
    public void RefusesAnAlignmentThatIsNotAPowerOfTwoUpTo4096(uint alignment)
    {
        var gguf = new Gguf();
        gguf.UInt32("general.alignment", alignment);
        Assert.Throws<InvalidDataException>(() => GgufHeader.Read(new MemoryStream(gguf.Build(dataBytes: 0))));
    }

    // A GGUF v3 header writer: metadata values, then tensor entries, padded to 32 bytes and followed by the data section.
    private sealed class Gguf
    {
        private readonly List<(string Key, Action<BinaryWriter> Write)> _values = [];
        private readonly List<(string Name, ulong[] Shape, uint Type, ulong Offset)> _tensors = [];

        public ulong? KeyCountOverride { get; init; }
        public ulong? TensorCountOverride { get; init; }

        public void Raw(string key, Action<BinaryWriter> write) => _values.Add((key, write));
        public void String(string key, string value) => Raw(key, writer => { writer.Write(8u); Text(writer, value); });
        public void UInt32(string key, uint value) => Raw(key, writer => { writer.Write(4u); writer.Write(value); });
        public void Int32(string key, int value) => Raw(key, writer => { writer.Write(5u); writer.Write(value); });
        public void Float32(string key, float value) => Raw(key, writer => { writer.Write(6u); writer.Write(value); });
        public void Bool(string key, bool value) => Raw(key, writer => { writer.Write(7u); writer.Write((byte)(value ? 1 : 0)); });
        public void UInt64(string key, ulong value) => Raw(key, writer => { writer.Write(10u); writer.Write(value); });
        public void Strings(string key, string[] values) => Raw(key, writer =>
        {
            writer.Write(9u); writer.Write(8u); writer.Write((ulong)values.Length);
            foreach (string value in values) Text(writer, value);
        });
        public void Int32s(string key, int[] values) => Raw(key, writer =>
        {
            writer.Write(9u); writer.Write(5u); writer.Write((ulong)values.Length);
            foreach (int value in values) writer.Write(value);
        });
        public void Tensor(string name, ulong[] shape, uint type, ulong offset) => _tensors.Add((name, shape, type, offset));

        public byte[] Build(int dataBytes)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("GGUF"u8);
                writer.Write(3u);
                writer.Write(TensorCountOverride ?? (ulong)_tensors.Count);
                writer.Write(KeyCountOverride ?? (ulong)_values.Count);
                foreach ((string key, Action<BinaryWriter> write) in _values)
                {
                    Text(writer, key);
                    write(writer);
                }
                foreach ((string name, ulong[] shape, uint type, ulong offset) in _tensors)
                {
                    Text(writer, name);
                    writer.Write((uint)shape.Length);
                    foreach (ulong dimension in shape) writer.Write(dimension);
                    writer.Write(type);
                    writer.Write(offset);
                }
                writer.Write(new byte[(32 - stream.Length % 32) % 32]);
                writer.Write(new byte[dataBytes]);
            }
            return stream.ToArray();
        }

        private static void Text(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((ulong)bytes.Length);
            writer.Write(bytes);
        }
    }
}
