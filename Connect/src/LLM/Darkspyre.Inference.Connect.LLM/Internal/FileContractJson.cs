using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal abstract class FileEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int number))
            throw new JsonException("The file enum requires a defined numeric value.");
        T value = (T)Enum.ToObject(typeof(T), number);
        return Enum.IsDefined(value) ? value : throw new JsonException("The file enum value is undefined.");
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("The file enum value is undefined.");
        writer.WriteNumberValue(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
    }
}

internal sealed class FileOrderJsonConverter : FileEnumJsonConverter<LlmFileOrder>;
internal sealed class FileSortJsonConverter : FileEnumJsonConverter<LlmFileSort>;
internal sealed class FileContentFormatJsonConverter : FileEnumJsonConverter<LlmFileContentFormat>;

internal sealed class FileRuntimeJsonConverter<T>(string message = "File and skill runtime inputs are not portable data.") : JsonConverter<T> where T : class
{
    public override bool HandleNull => true;
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException(message);
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else throw new NotSupportedException(message);
    }
}
