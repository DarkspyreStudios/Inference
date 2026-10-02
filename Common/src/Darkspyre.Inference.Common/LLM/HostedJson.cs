using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Common.LLM;

internal abstract class HostedEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int number)) { throw new JsonException("The hosted enum requires a defined numeric value."); }
        T value = (T)Enum.ToObject(typeof(T), number);
        return Enum.IsDefined(value) ? value : throw new JsonException("The hosted enum value is undefined.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) { throw new JsonException("The hosted enum value is undefined."); }
        writer.WriteNumberValue(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
    }
}

internal sealed class HostedToolPhaseJsonConverter : HostedEnumJsonConverter<LlmHostedToolPhase>;
internal sealed class ImageGenerationActionJsonConverter : HostedEnumJsonConverter<LlmImageGenerationAction>;

internal sealed class HostedToolKindJsonConverter : HostedEnumJsonConverter<LlmHostedToolKind>
{
    // Keep the standard enum dictionary-key syntax without reflection-based metadata.
    private readonly JsonConverter<LlmHostedToolKind> propertyNames =
        JsonMetadataServices.GetEnumConverter<LlmHostedToolKind>(new JsonSerializerOptions());

    public override LlmHostedToolKind ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        LlmHostedToolKind value = propertyNames.ReadAsPropertyName(ref reader, typeToConvert, options);
        return Enum.IsDefined(value) ? value : throw new JsonException("The hosted enum value is undefined.");
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, LlmHostedToolKind value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) { throw new JsonException("The hosted enum value is undefined."); }
        propertyNames.WriteAsPropertyName(writer, value, options);
    }
}

internal sealed class HostedImageDataJsonConverter(JsonConverter<ReadOnlyMemory<byte>> dataConverter) : JsonConverter<ReadOnlyMemory<byte>>
{
    public override bool HandleNull => true;
    public override ReadOnlyMemory<byte> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) { throw new JsonException("Required binary data cannot be null."); }
        return dataConverter.Read(ref reader, typeToConvert, options);
    }

    public override void Write(Utf8JsonWriter writer, ReadOnlyMemory<byte> value, JsonSerializerOptions options) => dataConverter.Write(writer, value, options);
}
