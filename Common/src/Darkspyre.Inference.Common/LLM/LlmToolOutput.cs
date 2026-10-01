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
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmToolOutputSessionResource([property: JsonRequired] InferenceResourceReference Resource) : LlmToolOutputPart;

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
    public IReadOnlyList<LlmToolOutputPart> Parts { get; init; } = [];

    /// <summary>A JSON object, or null.</summary>
    public JsonElement? StructuredContent { get; init; }

    public bool IsError { get; init; }

    public static LlmToolOutput FromText(string text, bool isError = false) =>
        new() { Parts = [new LlmToolOutputText(text)], IsError = isError };
}
