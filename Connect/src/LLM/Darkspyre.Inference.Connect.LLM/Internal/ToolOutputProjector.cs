using System.Text;
using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>A part as a route sends it. Text-carried content has already been rendered to text.</summary>
internal abstract record SentToolPart;

internal sealed record SentText(string Text) : SentToolPart;

internal sealed record SentImage(string MediaType, ReadOnlyMemory<byte> Data, string? FileName) : SentToolPart;

/// <summary>A document the provider reads as a file: a PDF, or UTF-8 text.</summary>
internal sealed record SentFile(string MediaType, ReadOnlyMemory<byte> Data, string FileName) : SentToolPart
{
    public bool IsPdf => MediaType == "application/pdf";
}

internal sealed record SentResourceLink(string Uri, string? Name, string? MediaType) : SentToolPart;

internal sealed record SentEmbeddedText(string Uri, string? MediaType, string Text) : SentToolPart;

internal sealed record SentNativeFile(string FileId) : SentToolPart;

internal sealed record SentStructured(JsonElement Value) : SentToolPart;

/// <summary>A projected tool output: what is sent, whether the error flag goes natively, and the report.</summary>
internal sealed record ProjectedToolOutput(
    IReadOnlyList<SentToolPart> Parts,
    bool IsError,
    bool NativeErrorFlag,
    LlmToolOutputProjection Report)
{
    /// <summary>The text parts joined, for routes that carry a single string.</summary>
    public string Text => string.Join("\n", Parts.OfType<SentText>().Select(part => part.Text));
}

/// <summary>
/// Applies a route's <see cref="LlmRichContentSupported"/> to one output. Adapters serialize through it, so
/// the report describes exactly what is sent. Unresolved workspace content requires a materializing route;
/// it cannot be silently omitted like an unsupported inline display part.
/// </summary>
internal static class ToolOutputProjector
{
    public const string ErrorMarker = "[Tool error]";

    public static ProjectedToolOutput Project(
        LlmToolOutput output,
        LlmRichContentSupported route,
        LlmRichContentSupported model,
        Func<Common.Resources.InferenceResourceReference, SentToolPart>? resource = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var sent = new List<SentToolPart>();
        var report = new List<LlmToolPartProjection>(output.Parts.Count);
        bool nativeError = false;
        LlmToolPartProjection errorFlag = new(LlmToolPartDelivery.Delivered);
        if (output.IsError)
        {
            (errorFlag, nativeError) = model.ErrorFlag switch
            {
                LlmRichContentCarriage.Native => (errorFlag, true),
                LlmRichContentCarriage.Text => (new LlmToolPartProjection(LlmToolPartDelivery.Converted), false),
                _ => (Omit(route.ErrorFlag), false)
            };
            if (errorFlag.Delivery == LlmToolPartDelivery.Converted)
            {
                sent.Add(new SentText(ErrorMarker));
            }
        }

        foreach (LlmToolOutputPart part in output.Parts)
        {
            report.Add(part switch
            {
                LlmToolOutputText text => Add(sent, new SentText(text.Text), LlmToolPartDelivery.Delivered),
                LlmToolOutputBinary binary => Binary(sent, binary.Kind, binary.MediaType, binary.Data, binary.FileName, route, model),
                LlmToolOutputResource embedded => Resource(sent, embedded, route, model),
                LlmToolOutputSessionResource logical when resource is not null => Add(sent, resource(logical.Resource), LlmToolPartDelivery.Delivered),
                LlmToolOutputSessionResource => throw new NotSupportedException("This route cannot materialize workspace tool content."),
                _ => new LlmToolPartProjection(LlmToolPartDelivery.Omitted, LlmToolPartOmission.InvalidContent)
            });
        }

        LlmToolPartProjection? structured = output.StructuredContent is not { } value ? null
            : value.ValueKind != JsonValueKind.Object
                ? new(LlmToolPartDelivery.Omitted, LlmToolPartOmission.InvalidContent)
                : model.StructuredContent switch
                {
                    LlmRichContentCarriage.Native => Add(sent, new SentStructured(value.Clone()), LlmToolPartDelivery.Delivered),
                    LlmRichContentCarriage.Text => Add(sent, new SentText(value.GetRawText()), LlmToolPartDelivery.Converted),
                    _ => Omit(route.StructuredContent)
                };

        return new(sent, output.IsError, nativeError, new(report, structured, errorFlag));
    }

    private static LlmToolPartProjection Resource(
        List<SentToolPart> sent,
        LlmToolOutputResource resource,
        LlmRichContentSupported route,
        LlmRichContentSupported model)
    {
        if (string.IsNullOrWhiteSpace(resource.Uri) || (resource.Text is not null && resource.Data is not null))
        {
            return new(LlmToolPartDelivery.Omitted, LlmToolPartOmission.InvalidContent);
        }
        if (resource is { Data: { } data, BodyKind: { } kind })
        {
            string mediaType = resource.MediaType ?? "application/octet-stream";
            return Binary(sent, kind, mediaType, data, resource.Name ?? FileNameOf(resource.Uri), route, model);
        }
        if (resource.Text is { } text)
        {
            return model.EmbeddedTextResources switch
            {
                LlmRichContentCarriage.Native => Add(sent, new SentEmbeddedText(resource.Uri, resource.MediaType, text),
                    LlmToolPartDelivery.Delivered),
                LlmRichContentCarriage.Text => Add(sent, new SentText($"[Resource: {resource.Uri}]\n{text}"),
                    LlmToolPartDelivery.Converted),
                _ => Omit(route.EmbeddedTextResources)
            };
        }
        return model.ResourceLinks switch
        {
            LlmRichContentCarriage.Native => Add(sent, new SentResourceLink(resource.Uri, resource.Name, resource.MediaType),
                LlmToolPartDelivery.Delivered),
            LlmRichContentCarriage.Text => Add(sent, new SentText($"[Resource link: {resource.Name ?? resource.Uri}] {resource.Uri}"),
                LlmToolPartDelivery.Converted),
            _ => Omit(route.ResourceLinks)
        };
    }

    private static LlmToolPartProjection Binary(
        List<SentToolPart> sent,
        LlmBinaryKind kind,
        string mediaType,
        ReadOnlyMemory<byte> data,
        string? fileName,
        LlmRichContentSupported route,
        LlmRichContentSupported model)
    {
        if (data.IsEmpty)
        {
            return new(LlmToolPartDelivery.Omitted, LlmToolPartOmission.Empty);
        }
        if (model.MaxBinaryBytes is long limit && data.Length > limit)
        {
            return new(LlmToolPartDelivery.Omitted, LlmToolPartOmission.InvalidContent);
        }
        switch (kind)
        {
            case LlmBinaryKind.Image:
                if (model.Images != LlmRichContentCarriage.Native)
                {
                    return Omit(route.Images);
                }
                return model.ImageMediaTypes.Contains(mediaType)
                    ? Add(sent, new SentImage(mediaType, data, fileName), LlmToolPartDelivery.Delivered)
                    : new(LlmToolPartDelivery.Omitted, LlmToolPartOmission.UnsupportedByRoute);
            case LlmBinaryKind.Document when mediaType == "application/pdf":
                return model.PdfDocuments == LlmRichContentCarriage.Native
                    ? Add(sent, new SentFile(mediaType, data, fileName ?? "document.pdf"), LlmToolPartDelivery.Delivered)
                    : Omit(route.PdfDocuments);
            case LlmBinaryKind.Document when LlmToolOutputResource.IsTextMediaType(mediaType):
                if (!TryDecode(data, out string text))
                {
                    return new(LlmToolPartDelivery.Omitted, LlmToolPartOmission.InvalidContent);
                }
                string name = fileName ?? "document.txt";
                return model.TextDocuments switch
                {
                    LlmRichContentCarriage.Native => Add(sent, new SentFile(mediaType, data, name), LlmToolPartDelivery.Delivered),
                    // A route with native embedded text resources reads a text document best in that form.
                    LlmRichContentCarriage.Text when model.EmbeddedTextResources == LlmRichContentCarriage.Native =>
                        Add(sent, new SentEmbeddedText($"file:///{name}", mediaType, text), LlmToolPartDelivery.Converted),
                    LlmRichContentCarriage.Text => Add(sent, new SentText($"[File: {name}]\n{text}"), LlmToolPartDelivery.Converted),
                    _ => Omit(route.TextDocuments)
                };
            case LlmBinaryKind.Audio:
                // No route carries audio natively; every route declares it Unsupported.
                return Omit(route.Audio);
            default:
                return Omit(route.OtherBinary);
        }
    }

    private static LlmToolPartProjection Add(List<SentToolPart> sent, SentToolPart part, LlmToolPartDelivery delivery)
    {
        sent.Add(part);
        return new(delivery);
    }

    private static LlmToolPartProjection Omit(LlmRichContentCarriage routeCarriage) => new(LlmToolPartDelivery.Omitted,
        routeCarriage == LlmRichContentCarriage.Unsupported ? LlmToolPartOmission.UnsupportedByRoute : LlmToolPartOmission.UnsupportedByModel);

    private static bool TryDecode(ReadOnlyMemory<byte> data, out string text)
    {
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data.Span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static string FileNameOf(string uri)
    {
        string name = uri.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        return name.Length == 0 ? "resource" : name;
    }
}
