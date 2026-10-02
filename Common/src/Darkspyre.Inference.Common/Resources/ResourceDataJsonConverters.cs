using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Media;

namespace Darkspyre.Inference.Common.Resources;

internal abstract class ResourceDataEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int number))
            throw new JsonException("Resource enum data must be a defined integer.");
        T value = (T)Enum.ToObject(typeof(T), number);
        return Enum.IsDefined(value) ? value : throw new JsonException("The resource enum value is undefined.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("The resource enum value is undefined.");
        writer.WriteNumberValue(Convert.ToInt32(value, CultureInfo.InvariantCulture));
    }
}

internal sealed class MediaSourceKindJsonConverter : ResourceDataEnumJsonConverter<InferenceMediaSourceKind>;
internal sealed class ResourceOperationPhaseJsonConverter : ResourceDataEnumJsonConverter<InferenceResourceOperationPhase>;
internal sealed class ResourceCommitOutcomeJsonConverter : ResourceDataEnumJsonConverter<InferenceResourceCommitOutcome>;
