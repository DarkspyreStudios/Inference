using System.Collections.Frozen;
using Darkspyre.Inference.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>How a route carries one kind of tool-output content.</summary>
internal enum LlmRichContentCarriage
{
    /// <summary>The route cannot carry it; projection omits it.</summary>
    Unsupported,

    /// <summary>Carried as the provider's own content item.</summary>
    Native,

    /// <summary>Carried as a text part that renders it.</summary>
    Text
}

/// <summary>What a host needs to know to send tool output on a route.</summary>
internal sealed record LlmRichContentSupported
{
    public LlmRichContentCarriage Images { get; init; }

    public IReadOnlySet<string> ImageMediaTypes { get; init; } = FrozenSet<string>.Empty;

    public LlmRichContentCarriage PdfDocuments { get; init; }

    /// <summary>UTF-8 text with a <c>text/*</c> or <c>application/json</c> media type.</summary>
    public LlmRichContentCarriage TextDocuments { get; init; }

    public LlmRichContentCarriage Audio { get; init; }

    public LlmRichContentCarriage OtherBinary { get; init; }

    public LlmRichContentCarriage ResourceLinks { get; init; }

    public LlmRichContentCarriage EmbeddedTextResources { get; init; }

    public LlmRichContentCarriage StructuredContent { get; init; }

    public LlmRichContentCarriage ErrorFlag { get; init; }

    /// <summary>Parts reach the provider in the order supplied.</summary>
    public InferenceSupport PreservesOrder { get; init; }

    /// <summary>An output with no parts is accepted.</summary>
    public InferenceSupport AllowsEmpty { get; init; }

    /// <summary>The largest binary part the route accepts, when known.</summary>
    public long? MaxBinaryBytes { get; init; }

    /// <summary>This support after the connection's and the model's overrides.</summary>
    internal LlmRichContentSupported For(LlmConnection connection, string? modelId)
    {
        LlmRichContentSupported result = Restrict(
            connection.CapabilityOverrides?.SupportsToolResultImages, connection.CapabilityOverrides?.SupportsToolResultDocuments);
        return modelId is not null && connection.ModelCapabilityOverrides.TryGetValue(modelId, out LlmCapabilityOverrides? model)
            ? result.Restrict(model.SupportsToolResultImages, model.SupportsToolResultDocuments)
            : result;
    }

    /// <summary>Lowers carriages that an override disables; an override never raises a carriage.</summary>
    internal LlmRichContentSupported Restrict(InferenceSupport? images, InferenceSupport? documents) => this with
    {
        Images = images == InferenceSupport.Unsupported ? LlmRichContentCarriage.Unsupported : Images,
        PdfDocuments = documents == InferenceSupport.Unsupported ? LlmRichContentCarriage.Unsupported : PdfDocuments,
        TextDocuments = documents == InferenceSupport.Unsupported ? LlmRichContentCarriage.Unsupported : TextDocuments
    };
}

internal enum LlmToolPartDelivery
{
    Delivered,
    Converted,
    Omitted
}

internal enum LlmToolPartOmission
{
    UnsupportedByRoute,
    UnsupportedByModel,
    InvalidContent,
    Empty
}

internal sealed record LlmToolPartProjection(LlmToolPartDelivery Delivery, LlmToolPartOmission? Omission = null);

/// <summary>What one tool output sends on a route: one entry per supplied part, in order.</summary>
internal sealed record LlmToolOutputProjection(
    IReadOnlyList<LlmToolPartProjection> Parts,
    LlmToolPartProjection? StructuredContent,
    LlmToolPartProjection ErrorFlag);
