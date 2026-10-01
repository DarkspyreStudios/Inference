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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmFileReference([property: JsonRequired] ProviderFileReference File) : LlmMessageAttachment;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmResourceAttachment([property: JsonRequired] InferenceResourceReference Resource) : LlmMessageAttachment;
