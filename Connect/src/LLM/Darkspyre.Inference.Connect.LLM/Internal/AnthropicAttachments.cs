using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class AnthropicAttachments
{
    public static JsonObject BuildContentBlock(LlmAttachment attachment) => attachment.Kind switch
    {
        LlmBinaryKind.Image => BuildImage(attachment),
        LlmBinaryKind.Document => BuildDocument(attachment),
        _ => throw new ArgumentOutOfRangeException(nameof(attachment))
    };

    private static JsonObject BuildImage(LlmAttachment attachment)
    {
        string mediaType = LlmAttachments.ResolveMediaType(attachment);
        if (mediaType is not (
            "image/jpeg" or "image/png" or "image/gif" or "image/webp"))
        {
            throw new NotSupportedException(
                $"Anthropic does not support '{mediaType}' image attachments.");
        }

        return Image(mediaType, LlmAttachments.ToBase64(attachment));
    }

    private static JsonObject BuildDocument(LlmAttachment attachment)
    {
        string mediaType = LlmAttachments.ResolveMediaType(attachment);
        JsonObject source;
        if (string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            source = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = "application/pdf",
                ["data"] = LlmAttachments.ToBase64(attachment)
            };
        }
        else if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase))
        {
            source = new JsonObject
            {
                ["type"] = "text",
                ["media_type"] = "text/plain",
                ["data"] = LlmAttachments.ReadText(attachment)
            };
        }
        else
        {
            throw new NotSupportedException(
                $"Anthropic cannot inline '{mediaType}' document attachments.");
        }

        return Document(source, attachment.FileName);
    }

    /// <summary>The block for projected tool output, which has already been checked against the route.</summary>
    public static JsonObject BuildToolPart(SentToolPart part) => part switch
    {
        SentImage image => Image(image.MediaType, Convert.ToBase64String(image.Data.Span)),
        SentFile { IsPdf: true } file => Document(new JsonObject
        {
            ["type"] = "base64",
            ["media_type"] = "application/pdf",
            ["data"] = Convert.ToBase64String(file.Data.Span)
        }, file.FileName),
        SentFile file => Document(new JsonObject
        {
            ["type"] = "text",
            ["media_type"] = "text/plain",
            ["data"] = System.Text.Encoding.UTF8.GetString(file.Data.Span)
        }, file.FileName),
        _ => throw new InvalidOperationException("Anthropic tool results do not carry this part natively.")
    };

    private static JsonObject Image(string mediaType, string data) => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = data }
    };

    private static JsonObject Document(JsonObject source, string title) => new()
    {
        ["type"] = "document",
        ["source"] = source,
        ["title"] = title
    };
}
