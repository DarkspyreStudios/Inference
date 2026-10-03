using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.Realtime;

internal sealed class RealtimeEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int number)) throw new JsonException("A defined realtime enum number is required.");
        T value = (T)Enum.ToObject(typeof(T), number);
        return Enum.IsDefined(value) ? value : throw new JsonException("The realtime enum value is undefined.");
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("The realtime enum value is undefined.");
        writer.WriteNumberValue(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
    }
}

internal sealed class RealtimeProviderOptionsJsonConverter : JsonConverter<RealtimeProviderOptions>
{
    public override bool HandleNull => true;
    public override RealtimeProviderOptions? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Common does not admit concrete realtime provider options.");
    public override void Write(Utf8JsonWriter writer, RealtimeProviderOptions value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else throw new NotSupportedException("Common does not admit concrete realtime provider options.");
    }
}

internal sealed class RealtimeSessionJsonConverter : RuntimeResourceJsonConverter<IRealtimeVoiceSession>;

internal sealed class RealtimeNullableAudioJsonConverter(JsonConverter<ReadOnlyMemory<byte>> bytes) : JsonConverter<ReadOnlyMemory<byte>?>
{
    public override bool HandleNull => true;
    public override ReadOnlyMemory<byte>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : bytes.Read(ref reader, typeof(ReadOnlyMemory<byte>), options);

    public override void Write(Utf8JsonWriter writer, ReadOnlyMemory<byte>? value, JsonSerializerOptions options)
    {
        if (value is { } audio) bytes.Write(writer, audio, options);
        else writer.WriteNullValue();
    }
}
