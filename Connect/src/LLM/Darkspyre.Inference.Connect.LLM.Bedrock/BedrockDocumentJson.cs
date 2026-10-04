using System.Buffers;
using System.Text;
using System.Text.Json;
using Amazon.Runtime.Documents;
using Amazon.Runtime.Documents.Internal.Transform;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal static class BedrockDocumentJson
{
    public static string Write(Document? document)
    {
        if (document is null)
            return "{}";

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        DocumentMarshaller.Instance.Write(writer, document.Value);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
