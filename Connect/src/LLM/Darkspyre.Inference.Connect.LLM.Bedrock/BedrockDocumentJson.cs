using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Runtime.Documents;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal static class BedrockDocumentJson
{
    public static Document Read(string json) => JsonSerializer.Deserialize(json, BedrockDocumentJsonContext.Default.Document);
    public static string Write(Document? document) => document is null ? "{}"
        : JsonSerializer.Serialize(document.Value, BedrockDocumentJsonContext.Default.Document);
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(DocumentJsonConverter)])]
[JsonSerializable(typeof(Document))]
internal sealed partial class BedrockDocumentJsonContext : JsonSerializerContext;
