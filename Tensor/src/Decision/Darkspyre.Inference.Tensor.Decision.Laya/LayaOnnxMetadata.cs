using System.Globalization;
using System.Text;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

/// <summary>Bounded metadata-only ONNX protobuf inspection. Tensor payloads are skipped, never deserialized.
/// Field numbers follow https://github.com/onnx/onnx/blob/main/onnx/onnx.proto.</summary>
internal sealed class LayaOnnxMetadata(Stream stream)
{
    private int _fields;
    private readonly record struct Field(int Number, int Wire, long Start, long End, ulong Value);

    internal static void Validate(Stream stream, int maximumLength)
    {
        var parser = new LayaOnnxMetadata(stream);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var inputs = new Dictionary<string, long[]>(StringComparer.Ordinal);
        bool graph = false;
        foreach (Field field in parser.Fields(stream.Length))
        {
            if (field.Number == 14)
            {
                string? key = null, value = null;
                foreach (Field entry in parser.Children(field))
                {
                    if (entry.Number == 1) key = parser.Text(entry);
                    if (entry.Number == 2) value = parser.Text(entry);
                }
                if (key is null || value is null || !metadata.TryAdd(key, value)) throw new InvalidDataException("Invalid ONNX metadata map.");
            }
            else if (field.Number == 7)
            {
                if (graph) throw new InvalidDataException("Duplicate ONNX graph.");
                graph = true;
                foreach (Field item in parser.Children(field))
                {
                    if (item.Number == 5)
                    {
                        foreach (Field tensor in parser.Children(item))
                            if (tensor.Number == 13 || tensor.Number == 14 && tensor.Value != 0)
                                throw new InvalidDataException("Laya requires embedded ONNX weights.");
                    }
                    if (item.Number != 11) continue;
                    string? name = null;
                    long[]? dimensions = null;
                    foreach (Field info in parser.Children(item))
                    {
                        if (info.Number == 1) name = parser.Text(info);
                        if (info.Number != 2) continue;
                        foreach (Field type in parser.Children(info))
                        {
                            if (type.Number != 1) continue;
                            foreach (Field tensor in parser.Children(type))
                            {
                                if (tensor.Number != 2) continue;
                                var dims = new List<long>();
                                foreach (Field dim in parser.Children(tensor))
                                {
                                    if (dim.Number != 1) continue;
                                    long? size = null;
                                    foreach (Field value in parser.Children(dim))
                                    {
                                        if (value.Number == 1 && value.Wire == 0) size = checked((long)value.Value);
                                        if (value.Number == 2) throw new InvalidDataException("Laya requires fixed ONNX input dimensions.");
                                    }
                                    dims.Add(size ?? throw new InvalidDataException("Missing ONNX dimension."));
                                    if (dims.Count > 8) throw new InvalidDataException("Unexpected ONNX input rank.");
                                }
                                dimensions = dims.ToArray();
                            }
                        }
                    }
                    if (name is null || dimensions is null || !inputs.TryAdd(name, dimensions)) throw new InvalidDataException("Invalid ONNX input.");
                }
            }
        }
        int ReadInt(string key) => metadata.TryGetValue(key, out string? value)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0
            ? number : throw new InvalidDataException("Missing or invalid Laya ONNX metadata.");
        int length = ReadInt(LayaOnnxModel.MaximumLengthKey), options = ReadInt(LayaOnnxModel.MaximumOptionsKey);
        if (!graph || length != maximumLength || !metadata.ContainsKey(LayaOnnxModel.ExportVersionKey)
            || !metadata.ContainsKey(LayaOnnxModel.SourceHashKey)) throw new InvalidDataException("Laya ONNX metadata disagrees with the agent configuration.");
        foreach ((string name, int size) in new[] { ("input_ids", length), ("attention_mask", length), ("marker_pos", options), ("marker_mask", options) })
            if (!inputs.TryGetValue(name, out long[]? shape) || shape is not [1, var actual] || actual != size)
                throw new InvalidDataException("Laya ONNX input shapes do not match its metadata.");
    }

    private IEnumerable<Field> Children(Field field)
    {
        if (field.Wire != 2) throw new InvalidDataException("Invalid ONNX protobuf message.");
        stream.Position = field.Start;
        return Fields(field.End);
    }

    private IEnumerable<Field> Fields(long end)
    {
        while (stream.Position < end)
        {
            if (++_fields > 1_000_000) throw new InvalidDataException("ONNX metadata exceeds the field budget.");
            ulong tag = Varint(end);
            int wire = (int)(tag & 7), number = checked((int)(tag >> 3));
            if (number == 0) throw new InvalidDataException("Invalid ONNX field.");
            ulong value = wire is 0 or 2 ? Varint(end) : 0;
            long start = stream.Position;
            long finish = wire switch { 0 => start, 1 => checked(start + 8), 5 => checked(start + 4), 2 => checked(start + (long)value), _ => throw new InvalidDataException("Unsupported protobuf wire type.") };
            if (finish > end) throw new InvalidDataException("Truncated ONNX metadata.");
            yield return new(number, wire, start, finish, value);
            stream.Position = finish;
        }
    }

    private ulong Varint(long end)
    {
        ulong value = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if (stream.Position >= end) throw new InvalidDataException("Truncated protobuf integer.");
            int next = stream.ReadByte();
            if (next < 0 || shift == 63 && next > 1) throw new InvalidDataException("Invalid protobuf integer.");
            value |= (ulong)(next & 127) << shift;
            if ((next & 128) == 0) return value;
        }
        throw new InvalidDataException("Invalid protobuf integer.");
    }

    private string Text(Field field)
    {
        if (field.Wire != 2 || field.End - field.Start > 1024 * 1024) throw new InvalidDataException("Invalid ONNX metadata string.");
        stream.Position = field.Start;
        byte[] bytes = new byte[checked((int)(field.End - field.Start))];
        stream.ReadExactly(bytes);
        return new UTF8Encoding(false, true).GetString(bytes);
    }
}
