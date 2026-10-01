using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.Video;

internal abstract class VideoEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int number))
            throw new JsonException("A defined numeric video enum value is required.");
        T value = (T)Enum.ToObject(typeof(T), number);
        return Enum.IsDefined(value) ? value : throw new JsonException("The video enum value is undefined.");
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("The video enum value is undefined.");
        writer.WriteNumberValue(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
    }
}

internal sealed class VideoJobStateJsonConverter : VideoEnumJsonConverter<VideoJobState>;
internal sealed class VideoOperationJsonConverter : VideoEnumJsonConverter<VideoOperation>;

internal sealed class VideoProviderOptionsJsonConverter : JsonConverter<VideoProviderOptions>
{
    public override bool HandleNull => true;
    public override VideoProviderOptions? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Common does not admit concrete video provider options.");
    public override void Write(Utf8JsonWriter writer, VideoProviderOptions value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else throw new NotSupportedException("Common does not admit concrete video provider options.");
    }
}

internal sealed class VideoInferenceJsonConverter : RuntimeResourceJsonConverter<IVideoInference>;
