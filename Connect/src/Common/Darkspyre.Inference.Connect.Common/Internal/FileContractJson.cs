using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM.Internal;

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
