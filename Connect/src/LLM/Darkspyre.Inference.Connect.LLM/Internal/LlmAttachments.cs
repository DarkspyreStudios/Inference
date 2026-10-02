using System.Text;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

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

    /// <summary>Refuses unimplemented reference routes before authentication, catalog or provider effects.</summary>
    public static void ValidateReferences(LlmChatRequest request, Func<bool>? supportsFileReferences = null, bool supportsResources = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Resources is not null && !supportsResources)
            throw new NotSupportedException("This route does not materialize caller-owned resources.");
        bool? acceptsFiles = null;
        foreach (var message in request.Messages)
        {
            foreach (var attachment in message.Attachments)
            {
                if (attachment is LlmAttachment) { continue; }
                if (attachment is LlmResourceAttachment resource && supportsResources)
                {
                    _ = CopyResource(resource.Resource);
                    continue;
                }
                if (attachment is LlmFileReference { File: { } file }
                    && (acceptsFiles ??= supportsFileReferences?.Invoke() ?? false))
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(file.FileId);
                    continue;
                }
                _ = RequireBinary(attachment);
            }
            if (message.ToolResults.Any(result => result is LlmToolResult function
                && function.Output.Parts.Any(part => part is LlmToolOutputSessionResource)) && !supportsResources)
                throw new NotSupportedException("This route cannot materialize workspace tool content.");
        }
    }

    public static void Validate(
        IReadOnlyList<LlmMessageAttachment> attachments,
        bool supportsImages,
        bool supportsDocuments,
        string parameterName,
        bool supportsReferences = false)
    {
        ArgumentNullException.ThrowIfNull(attachments, parameterName);
        foreach (LlmMessageAttachment value in attachments)
        {
            if (value is null)
            {
                throw new ArgumentException("Attachments cannot contain null entries.", parameterName);
            }

            if (supportsReferences && value is LlmFileReference or LlmResourceAttachment)
            {
                if (!supportsDocuments) throw new NotSupportedException("The selected connection does not support document references.");
                _ = Copy(value);
                continue;
            }
            LlmAttachment attachment = RequireBinary(value);

            if (attachment.Kind is not (LlmBinaryKind.Image or LlmBinaryKind.Document))
            {
                throw new ArgumentException(
                    "Every prompt attachment must be an image or a document.",
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

            if (attachment.Kind == LlmBinaryKind.Image && !supportsImages)
            {
                throw new NotSupportedException(
                    "The selected connection does not support image attachments on this lane.");
            }

            if (attachment.Kind == LlmBinaryKind.Document && !supportsDocuments)
            {
                throw new NotSupportedException(
                    "The selected connection does not support document attachments on this lane.");
            }

            _ = ResolveMediaType(attachment);
        }
    }

    public static LlmAttachment RequireBinary(LlmMessageAttachment attachment) => attachment switch
    {
        LlmAttachment binary => binary,
        null => throw new ArgumentException("Attachments cannot contain null entries.", nameof(attachment)),
        _ => throw new NotSupportedException("This route does not support referenced attachments.")
    };

    public static LlmMessageAttachment Copy(LlmMessageAttachment attachment) => attachment switch
    {
        LlmAttachment binary => binary with { Data = binary.Data.ToArray() },
        LlmFileReference { File: { } file } => new LlmFileReference(CopyFile(file)),
        LlmResourceAttachment { Resource: { } resource } => new LlmResourceAttachment(CopyResource(resource)),
        _ => throw new ArgumentException("Unknown or invalid attachment.", nameof(attachment))
    };

    public static InferenceResourceReference CopyResource(InferenceResourceReference resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return new(resource.WorkspaceId, resource.Id);
    }

    private static ProviderFileReference CopyFile(ProviderFileReference file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file.FileId);
        return new(file.FileId);
    }

    /// <summary>The attachment's given media type, or the type detected from its bytes.</summary>
    public static string ResolveMediaType(LlmAttachment attachment) => attachment.MediaType ?? attachment.Kind switch
    {
        LlmBinaryKind.Image => ResolveImageMediaType(attachment.Data.Span),
        LlmBinaryKind.Document => ResolveDocumentMediaType(
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
