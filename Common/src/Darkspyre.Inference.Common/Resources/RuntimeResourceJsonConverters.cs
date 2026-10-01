using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.Resources;

internal abstract class RuntimeResourceJsonConverter<T> : JsonConverter<T> where T : class
{
    public override bool HandleNull => true;

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) { return null; }
        throw new NotSupportedException("Live resource access and handles are not portable JSON data.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        throw new NotSupportedException("Live resource access and handles are not portable JSON data.");
    }
}

internal sealed class ResourceAccessJsonConverter : RuntimeResourceJsonConverter<IInferenceResourceAccess>;
internal sealed class ResourceReadHandleJsonConverter : RuntimeResourceJsonConverter<IInferenceResourceReadHandle>;
internal sealed class ResourceWriteHandleJsonConverter : RuntimeResourceJsonConverter<IInferenceResourceWriteHandle>;
