using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.Media;

/// <summary>The kind of an <see cref="InferenceMediaSource"/>.</summary>
public enum InferenceMediaSourceKind
{
    /// <summary>Bytes supplied with the request.</summary>
    Binary = 0,
    /// <summary>A URL the provider fetches.</summary>
    Url = 1,
    /// <summary>A file already uploaded to the provider.</summary>
    ProviderFile = 2,
    /// <summary>A workspace resource.</summary>
    Resource = 3
}

/// <summary>Data-only source identity. No source variant resolves content or grants access.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(BinaryMediaSource), "binary")]
[JsonDerivedType(typeof(UrlMediaSource), "url")]
[JsonDerivedType(typeof(FileMediaSource), "file")]
[JsonDerivedType(typeof(ResourceMediaSource), "resource")]
public abstract record InferenceMediaSource;

/// <summary>Caller-owned input bytes, borrowed and not mutated during an entered request.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BinaryMediaSource(string? ContentType,
    [property: JsonRequired] ReadOnlyMemory<byte> Data, string? FileName = null) : InferenceMediaSource;

/// <summary>A provider-fetch input, not a request for library-side URL fetching.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UrlMediaSource([property: JsonRequired] Uri Url) : InferenceMediaSource;

/// <summary>A provider-native file ID. It proves neither portable ownership nor entitlement.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProviderFileReference([property: JsonRequired] string FileId);

/// <summary>A media source that refers to a file already uploaded to the provider.</summary>
/// <param name="File">The provider file the source refers to.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileMediaSource([property: JsonRequired] ProviderFileReference File) : InferenceMediaSource;

/// <summary>A media source that refers to a workspace resource.</summary>
/// <param name="Resource">The workspace resource the source refers to.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResourceMediaSource([property: JsonRequired] InferenceResourceReference Resource) : InferenceMediaSource;

/// <summary>Native receipt scope for selected endpoint, route and account authority; not content ownership.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InferenceResourceScope([property: JsonRequired] string Owner,
    [property: JsonRequired] string Identity);

/// <summary>A portable description of a failed resource operation.</summary>
/// <param name="Code">The classification of the failure.</param>
/// <param name="ProviderCode">The provider's own error code, when it reports one.</param>
/// <param name="Message">The failure message.</param>
/// <param name="Resource">The observed effects of the resource operation, when known.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InferenceResourceFailure([property: JsonRequired] InferenceFailureCode Code,
    string? ProviderCode, [property: JsonRequired] string Message,
    InferenceResourceOperationFailure? Resource = null);
