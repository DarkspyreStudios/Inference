using System.Text;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static class LlmAttachments
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private static readonly byte[] JpegSignature = [0xff, 0xd8, 0xff];
    private static readonly byte[] ZipFileSignature = [0x50, 0x4b, 0x03, 0x04];
    private static readonly byte[] ZipEmptySignature = [0x50, 0x4b, 0x05, 0x06];
    private static readonly byte[] ZipSpannedSignature = [0x50, 0x4b, 0x07, 0x08];
    private static readonly byte[] OleSignature = [0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1];

    public static void Validate(
        IReadOnlyList<LlmAttachment> attachments,
        bool supportsImages,
        bool supportsDocuments,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(attachments, parameterName);
        foreach (LlmAttachment attachment in attachments)
        {
            if (attachment is null)
            {
                throw new ArgumentException("Attachments cannot contain null entries.", parameterName);
            }

            if (!Enum.IsDefined(attachment.Kind))
            {
                throw new ArgumentException(
                    "Every attachment requires a supported attachment kind.",
                    parameterName);
            }

            if (string.IsNullOrWhiteSpace(attachment.FileName)
                || attachment.FileName is "." or ".."
                || attachment.FileName.IndexOfAny(['/', '\\', '\0']) >= 0)
            {
                throw new ArgumentException(
                    "Every attachment requires a file name without a directory path.",
                    parameterName);
            }

            if (attachment.Data.IsEmpty)
            {
                throw new ArgumentException("Attachments cannot be empty.", parameterName);
            }

            if (attachment.Kind == LlmAttachmentKind.Image && !supportsImages)
            {
                throw new NotSupportedException(
                    "The selected connection does not support image attachments on this lane.");
            }

            if (attachment.Kind == LlmAttachmentKind.Document && !supportsDocuments)
            {
                throw new NotSupportedException(
                    "The selected connection does not support document attachments on this lane.");
            }

            _ = ResolveMediaType(attachment);
        }
    }

    public static string ResolveMediaType(LlmAttachment attachment) => attachment.Kind switch
    {
        LlmAttachmentKind.Image => ResolveImageMediaType(attachment.Data.Span),
        LlmAttachmentKind.Document => ResolveDocumentMediaType(
            attachment.FileName,
            attachment.Data.Span),
        _ => throw new ArgumentOutOfRangeException(nameof(attachment))
    };

    public static string ReadText(LlmAttachment attachment)
    {
        try
        {
            return StrictUtf8.GetString(attachment.Data.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException(
                $"The document attachment '{attachment.FileName}' must contain valid UTF-8 text.",
                nameof(attachment),
                exception);
        }
    }

    public static string ToBase64(LlmAttachment attachment) =>
        Convert.ToBase64String(attachment.Data.Span);

    public static string ToDataUrl(LlmAttachment attachment) =>
        $"data:{ResolveMediaType(attachment)};base64,{ToBase64(attachment)}";

    private static string ResolveImageMediaType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(PngSignature))
        {
            return "image/png";
        }

        if (data.StartsWith(JpegSignature))
        {
            return "image/jpeg";
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (data.Length >= 12
            && data[..4].SequenceEqual("RIFF"u8)
            && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        throw new NotSupportedException(
            "The attachment data is not a supported PNG, JPEG, GIF, or WebP image.");
    }

    private static string ResolveDocumentMediaType(string fileName, ReadOnlySpan<byte> data)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (data.StartsWith("%PDF-"u8))
        {
            return "application/pdf";
        }

        bool isZip = data.StartsWith(ZipFileSignature)
            || data.StartsWith(ZipEmptySignature)
            || data.StartsWith(ZipSpannedSignature);
        if (isZip)
        {
            return extension switch
            {
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                _ => throw new NotSupportedException(
                    $"The document attachment '{fileName}' is a ZIP container with an unsupported file extension.")
            };
        }

        bool isOle = data.StartsWith(OleSignature);
        if (isOle)
        {
            return extension switch
            {
                ".doc" => "application/msword",
                ".xls" => "application/vnd.ms-excel",
                _ => throw new NotSupportedException(
                    $"The document attachment '{fileName}' is an OLE document with an unsupported file extension.")
            };
        }

        if (data.StartsWith("{\\rtf"u8))
        {
            return "application/rtf";
        }

        _ = ReadUtf8(fileName, data);
        return extension switch
        {
            ".csv" => "text/csv",
            ".htm" or ".html" => "text/html",
            ".json" => "application/json",
            ".md" or ".markdown" => "text/markdown",
            ".xml" => "application/xml",
            _ => "text/plain"
        };
    }

    private static string ReadUtf8(string fileName, ReadOnlySpan<byte> data)
    {
        try
        {
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException exception)
        {
            throw new NotSupportedException(
                $"The document attachment '{fileName}' is not a recognized supported document format.",
                exception);
        }
    }
}
