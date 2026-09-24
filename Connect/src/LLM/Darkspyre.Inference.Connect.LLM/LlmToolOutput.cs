using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>One ordered part of a tool's output. A captured context serializes each part with a <c>type</c> field.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmToolOutputText), "text")]
[JsonDerivedType(typeof(LlmToolOutputBinary), "binary")]
[JsonDerivedType(typeof(LlmToolOutputResource), "resource")]
public abstract record LlmToolOutputPart;

public sealed record LlmToolOutputText(string Text) : LlmToolOutputPart;

public enum LlmBinaryKind
{
    Image,
    Document,
    Audio,
    Other
}

public sealed record LlmToolOutputBinary(
    LlmBinaryKind Kind,
    string MediaType,
    ReadOnlyMemory<byte> Data,
    string? FileName = null) : LlmToolOutputPart;

/// <summary>A resource reference, with an optional embedded body. Connect never fetches it.</summary>
public sealed record LlmToolOutputResource(string Uri) : LlmToolOutputPart
{
    public string? Name { get; init; }

    public string? MediaType { get; init; }

    /// <summary>An embedded text body. With a null <see cref="Data"/> as well, the part is a link.</summary>
    public string? Text { get; init; }

    /// <summary>An embedded byte body, classified by <see cref="MediaType"/>.</summary>
    public ReadOnlyMemory<byte>? Data { get; init; }
}

/// <summary>A tool's output as the host returns it: ordered parts, structured data and an error flag.</summary>
public sealed record LlmToolOutput
{
    public IReadOnlyList<LlmToolOutputPart> Parts { get; init; } = [];

    /// <summary>A JSON object, or null.</summary>
    public JsonElement? StructuredContent { get; init; }

    public bool IsError { get; init; }

    public static LlmToolOutput FromText(string text, bool isError = false) =>
        new() { Parts = [new LlmToolOutputText(text)], IsError = isError };
}

/// <summary>How a route carries one kind of tool-output content.</summary>
public enum LlmRichContentCarriage
{
    /// <summary>The route cannot carry it; projection omits it.</summary>
    Unsupported,

    /// <summary>Carried as the provider's own content item.</summary>
    Native,

    /// <summary>Carried as a text part that renders it.</summary>
    Text
}

/// <summary>What a host needs to know to send tool output on a route.</summary>
public sealed record LlmRichContentSupported
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
    public bool PreservesOrder { get; init; }

    /// <summary>An output with no parts is accepted.</summary>
    public bool AllowsEmpty { get; init; }

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
    internal LlmRichContentSupported Restrict(bool? images, bool? documents) => this with
    {
        Images = images == false ? LlmRichContentCarriage.Unsupported : Images,
        PdfDocuments = documents == false ? LlmRichContentCarriage.Unsupported : PdfDocuments,
        TextDocuments = documents == false ? LlmRichContentCarriage.Unsupported : TextDocuments
    };
}

/// <summary>Which lane's support a projection applies: chat requests, or agent-session tool calls.</summary>
public enum LlmToolOutputLane
{
    Chat,
    AgentSession
}

public enum LlmToolPartDelivery
{
    Delivered,
    Converted,
    Omitted
}

public enum LlmToolPartOmission
{
    UnsupportedByRoute,
    UnsupportedByModel,
    InvalidContent,
    Empty
}

public sealed record LlmToolPartProjection(LlmToolPartDelivery Delivery, LlmToolPartOmission? Omission = null);

/// <summary>What one tool output sends on a route: one entry per supplied part, in order.</summary>
public sealed record LlmToolOutputProjection(
    IReadOnlyList<LlmToolPartProjection> Parts,
    LlmToolPartProjection? StructuredContent,
    LlmToolPartProjection ErrorFlag);
