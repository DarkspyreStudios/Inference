using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// The descriptors a local estimate counts caller-owned resources by. Estimates are synchronous, so each caller resolves
/// the descriptors before it measures; nothing is cached.
/// </summary>
internal static class ResourceSizes
{
    /// <summary>The map a measure without caller-owned resource access uses.</summary>
    internal static IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> None { get; } =
        FrozenDictionary<InferenceResourceReference, InferenceResourceDescriptor?>.Empty;

    /// <summary>Descriptors for the references, read through access.GetAsync; null for a reference access cannot read.</summary>
    internal static async Task<IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?>> ResolveAsync(
        IInferenceResourceAccess access, IEnumerable<InferenceResourceReference> references, CancellationToken cancellationToken)
    {
        var descriptors = new Dictionary<InferenceResourceReference, InferenceResourceDescriptor?>();
        foreach (InferenceResourceReference reference in references)
        {
            if (descriptors.ContainsKey(reference)) continue;
            descriptors[reference] = await access.GetAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        return descriptors;
    }

    /// <summary>The caller-owned resources one message sends: its resource attachments and workspace tool-output parts.</summary>
    internal static IEnumerable<InferenceResourceReference> References(LlmChatMessage message) =>
        message.Attachments.OfType<LlmResourceAttachment>().Select(attachment => attachment.Resource)
            .Concat(message.ToolResults.OfType<LlmToolResult>().SelectMany(result => result.Output.Parts)
                .OfType<LlmToolOutputSessionResource>().Select(part => part.Resource));

    /// <summary>
    /// The tokens of the resources a request sends: each distinct reference in <paramref name="counted"/>, and each map
    /// entry no message of <paramref name="all"/> references, which the request's resource selection adds. A reference
    /// with no descriptor counts 0.
    /// </summary>
    internal static long Tokens(IEnumerable<LlmChatMessage> counted, IEnumerable<LlmChatMessage> all,
        IReadOnlyDictionary<InferenceResourceReference, InferenceResourceDescriptor?> resources)
    {
        HashSet<InferenceResourceReference> referenced = [.. all.SelectMany(References)];
        long tokens = 0;
        foreach (InferenceResourceReference reference in counted.SelectMany(References).Distinct()
            .Concat(resources.Keys.Where(reference => !referenced.Contains(reference))))
        {
            if (resources.GetValueOrDefault(reference) is { } descriptor)
                tokens = checked(tokens + AttachmentTokens.Estimate(descriptor.ContentType, descriptor.Bytes));
        }
        return tokens;
    }
}
