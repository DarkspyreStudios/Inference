using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.STT;

internal sealed class SpeechAudioEncodingJsonConverter : JsonConverter<SpeechAudioEncoding>
{
    public override SpeechAudioEncoding Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value))
            throw new JsonException("A defined numeric speech encoding is required.");
        var encoding = (SpeechAudioEncoding)value;
        return Enum.IsDefined(encoding) ? encoding : throw new JsonException("Unknown speech encoding.");
    }
    public override void Write(Utf8JsonWriter writer, SpeechAudioEncoding value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown speech encoding.");
        writer.WriteNumberValue((int)value);
    }
}

internal sealed class SpeechTranscriptionClientJsonConverter : RuntimeResourceJsonConverter<ISpeechTranscriptionClient>;
internal sealed class SpeechTranscriptionSessionJsonConverter : RuntimeResourceJsonConverter<ISpeechTranscriptionSession>;
