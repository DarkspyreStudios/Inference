using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal sealed class DocumentRankingMetricJsonConverter : JsonConverter<XaiDocumentRankingMetric>
{
    public override XaiDocumentRankingMetric Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value)
            && Enum.IsDefined((XaiDocumentRankingMetric)value) ? (XaiDocumentRankingMetric)value
            : throw new JsonException("Unknown document ranking metric.");

    public override void Write(Utf8JsonWriter writer, XaiDocumentRankingMetric value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown document ranking metric.");
        writer.WriteNumberValue((int)value);
    }
}

internal sealed class DocumentRagPipelineJsonConverter : JsonConverter<XaiDocumentRagPipeline>
{
    public override XaiDocumentRagPipeline Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value)
            && Enum.IsDefined((XaiDocumentRagPipeline)value) ? (XaiDocumentRagPipeline)value
            : throw new JsonException("Unknown document RAG pipeline.");

    public override void Write(Utf8JsonWriter writer, XaiDocumentRagPipeline value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value)) throw new JsonException("Unknown document RAG pipeline.");
        writer.WriteNumberValue((int)value);
    }
}
