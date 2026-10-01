using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.Media;

public enum InferenceMediaSourceKind
{
    Binary = 0,
    Url = 1,
    ProviderFile = 2,
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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FileMediaSource([property: JsonRequired] ProviderFileReference File) : InferenceMediaSource;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResourceMediaSource([property: JsonRequired] InferenceResourceReference Resource) : InferenceMediaSource;

/// <summary>Native receipt scope for selected endpoint, route and account authority; not content ownership.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InferenceResourceScope([property: JsonRequired] string Owner,
    [property: JsonRequired] string Identity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InferenceResourceFailure([property: JsonRequired] InferenceFailureCode Code,
    string? ProviderCode, [property: JsonRequired] string Message,
    InferenceResourceOperationFailure? Resource = null);
