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
        Assert.Equal(("BF16", 0L, 12L), (entry.Dtype, entry.Begin, entry.End));
        Assert.Equal([2L, 3L], entry.Shape);
        Assert.Equal("pt", header.Metadata["format"]);
        Assert.Equal(file.Length - 12, header.DataOffset);
    }

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
