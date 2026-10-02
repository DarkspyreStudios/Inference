using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal sealed class DocumentSearchRuntimeJsonConverter : JsonConverter<IDocumentSearchClient>
{
    public override bool HandleNull => true;
    public override IDocumentSearchClient? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : throw new NotSupportedException("Live document-search clients are not portable data.");
    public override void Write(Utf8JsonWriter writer, IDocumentSearchClient value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else throw new NotSupportedException("Live document-search clients are not portable data.");
    }
}
