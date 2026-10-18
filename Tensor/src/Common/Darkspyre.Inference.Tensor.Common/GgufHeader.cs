using System.Collections.ObjectModel;
using System.Text;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Common;

/// <summary>
/// The header of a GGUF file, read from the start of a stream without reading tensor data: the version, the metadata
/// values, the tensor directory, the offset of the tensor data and the stream's length. Values carry the CLR types
/// <c>GgufFile.Metadata</c> reports for the same file (for example <c>uint</c> for UINT32 and <c>string[]</c> for an
/// array of strings).
/// </summary>
/// <param name="Version">The GGUF format version, 2 or later.</param>
/// <param name="Values">The metadata values by key.</param>
/// <param name="Tensors">The tensor directory by tensor name.</param>
/// <param name="DataOffset">The aligned offset where tensor data starts.</param>
/// <param name="FileLength">The stream's length, which must be the whole file's.</param>
public sealed record GgufHeader(uint Version, IReadOnlyDictionary<string, object> Values,
    IReadOnlyDictionary<string, GgufTensorInfo> Tensors, long DataOffset, long FileLength)
{
    private const uint Magic = 0x46554747; // "GGUF" in little-endian

    /// <summary>
    /// Reads the header through a <see cref="MetadataReadStream"/> bounded to <paramref name="maximumBytes"/>. Throws
    /// <see cref="InvalidDataException"/> for a malformed header, <see cref="NotSupportedException"/> for a version below 2
    /// or an unknown value type, and <see cref="IOException"/> when the stream ends before the header does. Before
    /// allocating, each key count, tensor count, rank, string length and array count is checked against the bytes left,
    /// at 13, 24, 8, 1 and the element's size bytes per item. A string longer than int.MaxValue, a rank above 4, a
    /// duplicate key or tensor, and an alignment that is not a power of two up to 4096 are refused.
    /// </summary>
    /// <param name="stream">A readable, seekable stream over the whole file; the caller keeps it.</param>
    /// <param name="maximumBytes">The most bytes the header may take.</param>
    public static GgufHeader Read(Stream stream, int maximumBytes = 16 * 1024 * 1024)
    {
        using var bounded = new MetadataReadStream(stream, maximumBytes);
        using var reader = new BinaryReader(bounded, Encoding.UTF8, leaveOpen: true);
        uint magic = reader.ReadUInt32();
        if (magic != Magic) throw new InvalidDataException($"Not a GGUF file (magic: 0x{magic:X8}).");
        uint version = reader.ReadUInt32();
        if (version < 2) throw new NotSupportedException($"GGUF version {version} is not supported.");

        ulong tensorCount = reader.ReadUInt64();
        ulong keyCount = reader.ReadUInt64();
        MetadataReadStream.RequireElements(reader, keyCount, 13);
        MetadataReadStream.RequireElements(reader, tensorCount, 24);

        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        for (ulong i = 0; i < keyCount; i++)
        {
            string key = ReadString(reader);
            object value = ReadValue(reader, (GgufValueType)reader.ReadUInt32());
            if (!values.TryAdd(key, value)) throw new InvalidDataException("Duplicate GGUF metadata key.");
        }

        var tensors = new Dictionary<string, GgufTensorInfo>(StringComparer.Ordinal);
        for (ulong i = 0; i < tensorCount; i++)
        {
            string name = ReadString(reader);
            uint rank = reader.ReadUInt32();
            if (rank > 4) throw new InvalidDataException("GGUF tensor rank exceeds the supported maximum.");
            MetadataReadStream.RequireElements(reader, rank, 8);
            var shape = new ulong[rank];
            for (uint d = 0; d < rank; d++) shape[d] = reader.ReadUInt64();
            var info = new GgufTensorInfo { Name = name, Shape = shape, Type = (GgmlTensorType)reader.ReadUInt32(), Offset = reader.ReadUInt64() };
            if (!tensors.TryAdd(name, info)) throw new InvalidDataException("Duplicate GGUF tensor name.");
        }

        long position = bounded.Position;
        int alignment = values.TryGetValue("general.alignment", out object? declared) ? Convert.ToInt32(declared, System.Globalization.CultureInfo.InvariantCulture) : 32;
        if (alignment <= 0 || alignment > 4096 || (alignment & (alignment - 1)) != 0)
            throw new InvalidDataException("Invalid GGUF alignment.");
        long dataOffset = position + (alignment - position % alignment) % alignment;
        return new(version, new ReadOnlyDictionary<string, object>(values),
            new ReadOnlyDictionary<string, GgufTensorInfo>(tensors), dataOffset, stream.Length);
    }

    private static string ReadString(BinaryReader reader)
    {
        ulong length = reader.ReadUInt64();
        MetadataReadStream.RequireElements(reader, length, 1);
        if (length == 0) return string.Empty;
        byte[] bytes = reader.ReadBytes((int)length);
        if (bytes.Length != (int)length) throw new EndOfStreamException("The GGUF header ends inside a string.");
        return Encoding.UTF8.GetString(bytes);
    }

    private static object ReadValue(BinaryReader reader, GgufValueType type) => type switch
    {
        GgufValueType.Uint8 => reader.ReadByte(),
        GgufValueType.Int8 => reader.ReadSByte(),
        GgufValueType.Uint16 => reader.ReadUInt16(),
        GgufValueType.Int16 => reader.ReadInt16(),
        GgufValueType.Uint32 => reader.ReadUInt32(),
        GgufValueType.Int32 => reader.ReadInt32(),
        GgufValueType.Float32 => reader.ReadSingle(),
        GgufValueType.Bool => reader.ReadByte() != 0,
        GgufValueType.String => ReadString(reader),
        GgufValueType.Uint64 => reader.ReadUInt64(),
        GgufValueType.Int64 => reader.ReadInt64(),
        GgufValueType.Float64 => reader.ReadDouble(),
        GgufValueType.Array => ReadArray(reader),
        _ => throw new NotSupportedException($"Unknown GGUF value type: {type}.")
    };

    private static object ReadArray(BinaryReader reader)
    {
        var type = (GgufValueType)reader.ReadUInt32();
        ulong count = reader.ReadUInt64();
        int minimumBytes = type switch
        {
            GgufValueType.Uint8 or GgufValueType.Int8 or GgufValueType.Bool => 1,
            GgufValueType.Uint16 or GgufValueType.Int16 => 2,
            GgufValueType.Uint32 or GgufValueType.Int32 or GgufValueType.Float32 => 4,
            GgufValueType.Uint64 or GgufValueType.Int64 or GgufValueType.Float64 or GgufValueType.String => 8,
            _ => throw new NotSupportedException("Unsupported GGUF metadata array type.")
        };
        MetadataReadStream.RequireElements(reader, count, minimumBytes);
        int n = (int)count;
        return type switch
        {
            GgufValueType.Uint8 => Fill(n, reader.ReadByte),
            GgufValueType.Int8 => Fill(n, reader.ReadSByte),
            GgufValueType.Uint16 => Fill(n, reader.ReadUInt16),
            GgufValueType.Int16 => Fill(n, reader.ReadInt16),
            GgufValueType.Uint32 => Fill(n, reader.ReadUInt32),
            GgufValueType.Int32 => Fill(n, reader.ReadInt32),
            GgufValueType.Float32 => Fill(n, reader.ReadSingle),
            GgufValueType.Bool => Fill(n, () => reader.ReadByte() != 0),
            GgufValueType.Uint64 => Fill(n, reader.ReadUInt64),
            GgufValueType.Int64 => Fill(n, reader.ReadInt64),
            GgufValueType.Float64 => Fill(n, reader.ReadDouble),
            _ => Fill(n, () => ReadString(reader))
        };
    }

    private static T[] Fill<T>(int count, Func<T> read)
    {
        var items = new T[count];
        for (int i = 0; i < count; i++) items[i] = read();
        return items;
    }
}
