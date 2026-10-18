using System.Text;

namespace Darkspyre.Inference.Tensor.Image.Supra.Tests;

public sealed class SafetensorsHeaderTests
{
    [Fact]
    public void ReadsTensorEntriesMetadataAndTheDataOffset()
    {
        byte[] file = File("""{"__metadata__":{"format":"pt"},"w":{"dtype":"BF16","shape":[2,3],"data_offsets":[0,12]}}""", 12);

        SafetensorsHeader header = SafetensorsHeader.Read(new MemoryStream(file));

        SafetensorsTensorEntry entry = Assert.Single(header.Tensors).Value;
        Assert.Equal(("BF16", new long[] { 2, 3 }, 0L, 12L), (entry.Dtype, entry.Shape, entry.Begin, entry.End));
        Assert.Equal("pt", header.Metadata["format"]);
        Assert.Equal(file.Length - 12, header.DataOffset);
    }

    [Fact]
    public void RefusesAHeaderLengthBeyondTheStream()
    {
        byte[] file = new byte[64];
        BitConverter.TryWriteBytes(file.AsSpan(0, 8), 1UL << 40);
        Assert.Throws<InvalidDataException>(() => SafetensorsHeader.Read(new MemoryStream(file)));
    }

    [Fact]
    public void RefusesAHeaderLengthBeyondTheLimit() =>
        Assert.Throws<InvalidDataException>(() => SafetensorsHeader.Read(new MemoryStream(
            File("""{"w":{"dtype":"F32","shape":[1],"data_offsets":[0,4]}}""", 4)), maximumBytes: 32));

    [Theory]
    [InlineData("""{"w":{"dtype":"F32","shape":[-1],"data_offsets":[0,4]}}""")]
    [InlineData("""{"w":{"dtype":"F32","shape":[1],"data_offsets":[4,8]}}""")]
    [InlineData("""{"w":{"dtype":"F32","shape":[2],"data_offsets":[0,4]}}""")]
    [InlineData("""{"w":{"dtype":"F32","shape":[1],"data_offsets":[0,4]},"w":{"dtype":"F32","shape":[1],"data_offsets":[0,4]}}""")]
    public void RefusesAMalformedTensorEntry(string json) =>
        Assert.Throws<InvalidDataException>(() => SafetensorsHeader.Read(new MemoryStream(File(json, 4))));

    // An 8-byte little-endian header length, the JSON header and a data section of the given size.
    private static byte[] File(string json, int dataBytes)
    {
        byte[] header = Encoding.UTF8.GetBytes(json);
        byte[] file = new byte[8 + header.Length + dataBytes];
        BitConverter.TryWriteBytes(file.AsSpan(0, 8), (ulong)header.Length);
        header.CopyTo(file, 8);
        return file;
    }
}
