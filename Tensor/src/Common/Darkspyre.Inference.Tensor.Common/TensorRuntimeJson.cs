using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Tensor.Common;

internal abstract class TensorRuntimeEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int number))
            throw new JsonException("The runtime state requires a defined numeric value.");
        T value = (T)Enum.ToObject(typeof(T), number);
        return Enum.IsDefined(value) ? value : throw new JsonException("The runtime state value is undefined.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("The runtime state value is undefined.");
        writer.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }
}

internal sealed class TensorRuntimeStateJsonConverter : TensorRuntimeEnumJsonConverter<TensorRuntimeState>;
