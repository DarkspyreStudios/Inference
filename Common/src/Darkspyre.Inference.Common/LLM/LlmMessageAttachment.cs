using System.Text.Json.Serialization;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>One ordered prompt attachment. References grant no access and contain no invented bytes.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LlmAttachment), "binary")]
[JsonDerivedType(typeof(LlmFileReference), "file")]
[JsonDerivedType(typeof(LlmResourceAttachment), "workspace-resource")]
public abstract record LlmMessageAttachment
{
    private protected LlmMessageAttachment() { }
}

/// <summary>A prompt attachment that refers to a file already uploaded to the provider.</summary>
/// <param name="File">The provider file the attachment refers to.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmFileReference([property: JsonRequired] ProviderFileReference File) : LlmMessageAttachment;

/// <summary>A prompt attachment that refers to a workspace resource.</summary>
/// <param name="Resource">The workspace resource the attachment refers to.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmResourceAttachment([property: JsonRequired] InferenceResourceReference Resource) : LlmMessageAttachment;
