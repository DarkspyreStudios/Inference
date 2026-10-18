using System.Collections.ObjectModel;
using System.Text.Json;
using Darkspyre.Inference.Tensor.Common;

namespace Darkspyre.Inference.Tensor.Image.Supra;

/// <summary>The JSON header of a safetensors file: an 8-byte little-endian length and a JSON document of tensor
/// entries and <c>__metadata__</c>.</summary>
/// <param name="Tensors">The tensor entries by name.</param>
/// <param name="Metadata">The <c>__metadata__</c> string map; empty when the header has none.</param>
/// <param name="DataOffset">The absolute offset of the data section: 8 plus the header length.</param>
internal sealed record SafetensorsHeader(IReadOnlyDictionary<string, SafetensorsTensorEntry> Tensors,
    IReadOnlyDictionary<string, string> Metadata, long DataOffset)
{
    /// <summary>Reads the header through a <see cref="MetadataReadStream"/> bounded to <paramref name="maximumBytes"/>.
    /// Throws <see cref="InvalidDataException"/> for a malformed header. It keeps the fork's checks
    /// (TSF:TensorSharp.Runtime/SafetensorsReader.cs:120-176): the 8-byte header length must fit the stream and
    /// <paramref name="maximumBytes"/> before any buffer is allocated; a negative dimension, offsets outside the data
    /// section, a byte length that differs from dtype and shape, and a duplicate tensor are refused. An unknown dtype
    /// throws <see cref="NotSupportedException"/>.</summary>
    internal static SafetensorsHeader Read(Stream stream, int maximumBytes = 16 * 1024 * 1024)
    {
        using var bounded = new MetadataReadStream(stream, maximumBytes);
        long fileLength = bounded.Length;
        Span<byte> lengthBytes = stackalloc byte[8];
        bounded.ReadExactly(lengthBytes);
        ulong headerLength = BitConverter.ToUInt64(lengthBytes);
        if (headerLength == 0 || headerLength > (ulong)Math.Max(0, Math.Min(bounded.Remaining, fileLength - 8)))
            throw new InvalidDataException($"The safetensors header length {headerLength} is out of range for a {fileLength}-byte file.");

        byte[] headerBytes = new byte[headerLength];
        bounded.ReadExactly(headerBytes);
        long dataOffset = 8 + (long)headerLength;
        long dataBytes = fileLength - dataOffset;
        var tensors = new Dictionary<string, SafetensorsTensorEntry>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(headerBytes);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("__metadata__"))
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty entry in property.Value.EnumerateObject())
                        metadata[entry.Name] = entry.Value.ToString();
                continue;
            }

            (string dtype, int size) = Dtype(property.Value.GetProperty("dtype").GetString());
            JsonElement shapeElement = property.Value.GetProperty("shape");
            var shape = new long[shapeElement.GetArrayLength()];
            int index = 0;
            foreach (JsonElement dimension in shapeElement.EnumerateArray()) shape[index++] = dimension.GetInt64();
            if (shape.Any(dimension => dimension < 0)) throw new InvalidDataException("Negative safetensors dimension.");

            JsonElement offsets = property.Value.GetProperty("data_offsets");
            long begin = offsets[0].GetInt64(), end = offsets[1].GetInt64();
            if (begin < 0 || end < begin || end > dataBytes)
                throw new InvalidDataException($"The safetensors tensor '{property.Name}' has data_offsets [{begin},{end}] outside the {dataBytes}-byte data section.");
            long elements = 1;
            foreach (long dimension in shape) elements = checked(elements * dimension);
            long expected = checked(elements * size);
            if (end - begin != expected)
                throw new InvalidDataException($"The safetensors tensor '{property.Name}' holds {end - begin} bytes; {dtype} of its shape needs {expected}.");
            if (!tensors.TryAdd(property.Name, new(dtype, shape, begin, end)))
                throw new InvalidDataException("Duplicate safetensors tensor name.");
        }
        return new(new ReadOnlyDictionary<string, SafetensorsTensorEntry>(tensors),
            new ReadOnlyDictionary<string, string>(metadata), dataOffset);
    }

    // The canonical dtype name and its element size, as the fork read them.
    private static (string Name, int Size) Dtype(string? value) => value?.ToUpperInvariant() switch
    {
        "F64" or "FLOAT64" => ("F64", 8),
        "F32" or "FLOAT32" => ("F32", 4),
        "F16" or "FLOAT16" => ("F16", 2),
        "BF16" or "BFLOAT16" => ("BF16", 2),
        "I64" or "INT64" => ("I64", 8),
        "I32" or "INT32" => ("I32", 4),
        "I16" or "INT16" => ("I16", 2),
        "I8" or "INT8" => ("I8", 1),
        "U8" or "UINT8" => ("U8", 1),
        "BOOL" => ("BOOL", 1),
        "F8_E4M3" or "F8_E4M3FN" => ("F8_E4M3", 1),
        "F8_E5M2" or "F8_E5M2FNUZ" => ("F8_E5M2", 1),
        _ => throw new NotSupportedException($"Unknown safetensors dtype '{value}'.")
    };
}

/// <summary>One tensor entry of a safetensors header.</summary>
/// <param name="Dtype">The canonical dtype name, such as <c>F32</c> or <c>BF16</c>.</param>
/// <param name="Shape">The row-major shape, outermost dimension first.</param>
/// <param name="Begin">The start of the tensor's bytes, relative to the data section.</param>
/// <param name="End">The end of the tensor's bytes, relative to the data section.</param>
internal sealed record SafetensorsTensorEntry(string Dtype, long[] Shape, long Begin, long End);
