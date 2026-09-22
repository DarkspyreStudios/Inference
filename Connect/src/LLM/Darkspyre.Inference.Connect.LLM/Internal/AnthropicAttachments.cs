using System.Text.Json.Nodes;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class AnthropicAttachments
{
    public static JsonObject BuildContentBlock(LlmAttachment attachment) => attachment.Kind switch
    {
        LlmAttachmentKind.Image => BuildImage(attachment),
        LlmAttachmentKind.Document => BuildDocument(attachment),
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

        return new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = mediaType,
                ["data"] = LlmAttachments.ToBase64(attachment)
            }
        };
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

        return new JsonObject
        {
            ["type"] = "document",
            ["source"] = source,
            ["title"] = attachment.FileName
        };
    }
}
