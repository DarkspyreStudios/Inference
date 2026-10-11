using System.Text.Json;
using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>One ordered part of a tool's output. A captured context serializes each part with a <c>type</c> field.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmToolOutputText), "text")]
[JsonDerivedType(typeof(LlmToolOutputBinary), "binary")]
[JsonDerivedType(typeof(LlmToolOutputResource), "resource")]
[JsonDerivedType(typeof(LlmToolOutputSessionResource), "workspace-resource")]
public abstract record LlmToolOutputPart;

/// <summary>Committed caller-owned content. Resolution requires the current operation's resource access.</summary>
/// <param name="Resource">The committed caller-owned resource.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmToolOutputSessionResource([property: JsonRequired] InferenceResourceReference Resource) : LlmToolOutputPart;

/// <summary>A plain text part of a tool's output.</summary>
/// <param name="Text">The text.</param>
public sealed record LlmToolOutputText(string Text) : LlmToolOutputPart;

/// <summary>The category of a binary body, derived from its media type.</summary>
public enum LlmBinaryKind
{
    /// <summary>An <c>image/*</c> body.</summary>
    Image,
    /// <summary>A PDF or text document body.</summary>
    Document,
    /// <summary>An <c>audio/*</c> body.</summary>
    Audio,
    /// <summary>Any other body.</summary>
    Other
}

/// <summary>An inline binary part of a tool's output.</summary>
/// <param name="Kind">The category of the bytes.</param>
/// <param name="MediaType">The media type of the bytes.</param>
/// <param name="Data">The bytes.</param>
/// <param name="FileName">The file name to present with the bytes, or null for none.</param>
public sealed record LlmToolOutputBinary(
    LlmBinaryKind Kind,
    string MediaType,
    ReadOnlyMemory<byte> Data,
    string? FileName = null) : LlmToolOutputPart;

/// <summary>A resource reference, with an optional embedded body. Connect never fetches it.</summary>
/// <param name="Uri">The URI that identifies the resource.</param>
public sealed record LlmToolOutputResource(string Uri) : LlmToolOutputPart
{
    /// <summary>A display name for the resource, or null for none.</summary>
    public string? Name { get; init; }

    /// <summary>The resource's media type, or null when unknown.</summary>
    public string? MediaType { get; init; }

    /// <summary>An embedded text body. With a null <see cref="Data"/> as well, the part is a link.</summary>
    public string? Text { get; init; }

    /// <summary>An embedded byte body, classified by <see cref="MediaType"/>.</summary>
    public ReadOnlyMemory<byte>? Data { get; init; }

    /// <summary>The binary kind <see cref="Data"/> takes from <see cref="MediaType"/>; null without a byte body.</summary>
    [JsonIgnore]
    public LlmBinaryKind? BodyKind => Data is null ? null : KindOf(MediaType);

    private static LlmBinaryKind KindOf(string? mediaType) => mediaType is null ? LlmBinaryKind.Other
        : mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? LlmBinaryKind.Image
        : mediaType == "application/pdf" || IsTextMediaType(mediaType) ? LlmBinaryKind.Document
        : mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? LlmBinaryKind.Audio
        : LlmBinaryKind.Other;

    /// <summary>A text document's media type: <c>text/*</c> or <c>application/json</c>.</summary>
    public static bool IsTextMediaType(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mediaType == "application/json";
}

/// <summary>A tool's output as the host returns it: ordered parts, structured data and an error flag.</summary>
public sealed record LlmToolOutput
{
    /// <summary>The output parts, in order.</summary>
    public IReadOnlyList<LlmToolOutputPart> Parts { get; init; } = [];

    /// <summary>A JSON object, or null.</summary>
    public JsonElement? StructuredContent { get; init; }

    /// <summary>True when the output reports a tool failure.</summary>
    public bool IsError { get; init; }

    /// <summary>Creates an output with one text part and the given error flag.</summary>
    public static LlmToolOutput FromText(string text, bool isError = false) =>
        new() { Parts = [new LlmToolOutputText(text)], IsError = isError };
}
