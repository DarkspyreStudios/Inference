using System.Buffers;
using System.Text;
using System.Text.Json;
using Amazon.Runtime.Documents;
using Amazon.Runtime.Documents.Internal.Transform;
using Amazon.Runtime.Internal.Transform;
using Amazon.Runtime.Internal.Util;

namespace Darkspyre.Inference.Connect.LLM.Bedrock;

internal static class BedrockDocumentJson
{
    public static Document Read(string json)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (JsonDocument input = JsonDocument.Parse(json))
        using (var writer = new Utf8JsonWriter(buffer))
        {
            // The SDK context requires a container root; unwrap this private envelope before use.
            writer.WriteStartObject();
            writer.WritePropertyName("value");
            input.RootElement.WriteTo(writer);
            writer.WriteEndObject();
            writer.Flush();
        }
        using var stream = new MemoryStream(buffer.WrittenMemory.ToArray(), writable: false);
        using var context = new JsonUnmarshallerContext(stream, maintainResponseBody: false, responseData: null);
        var reader = new StreamingUtf8JsonReader(stream, buffer.WrittenCount);
        try { return DocumentUnmarshaller.Instance.Unmarshall(context, ref reader).AsDictionary()["value"]; }
        finally
        {
            // The SDK returns its rented buffer only when the reader reaches EOF.
            while (reader.Read()) { }
        }
    }

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
