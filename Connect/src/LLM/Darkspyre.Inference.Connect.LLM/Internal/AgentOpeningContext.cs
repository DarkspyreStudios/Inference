using System.Text;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// A host context carried into a new provider-side conversation: a text transcript and the history's
/// attachments, sent once with the first turn.
/// </summary>
internal sealed record AgentOpeningContext(string Text, IReadOnlyList<LlmAttachment> Attachments, string Notice)
{
    public const string Header = "CONVERSATION SO FAR";

    /// <summary>The opening context for a session start, or null when the start supplies no entries.</summary>
    public static AgentOpeningContext? For(LlmAgentSessionOptions options, bool carriesAttachments)
    {
        if (options.Context is null)
        {
            return null;
        }
        if (options.UnboundContinuationState != LlmUnboundContinuationState.Omit)
        {
            throw new NotSupportedException(
                "This runtime starts a new provider conversation that carries no continuation state. "
                + "Start with UnboundContinuationState.Omit to carry the context as opening context.");
        }
        (LlmAgentContext context, int omitted) = ManagedAgentContext.WithoutContinuationState(options.Context);
        if (context.Entries.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        text.AppendLine(Header);
        TranscriptText.Append(text, context.Entries.Select(entry => entry.Message));
        // History images and documents travel as first-turn attachments; tool-output audio, other binaries and
        // resource byte bodies have no attachment form and count as not carried.
        LlmToolOutputPart[] toolBytes = [.. context.Entries.SelectMany(entry => entry.Message.ToolResults)
            .SelectMany(result => result.Output.Parts)
            .Where(part => part is LlmToolOutputBinary or LlmToolOutputResource { Data: not null })];
        LlmAttachment[] attachments = [.. context.Entries.SelectMany(entry => entry.Message.Attachments).Select(LlmAttachments.RequireBinary),
            .. toolBytes.OfType<LlmToolOutputBinary>()
                .Where(binary => binary.Kind is LlmBinaryKind.Image or LlmBinaryKind.Document)
                .Select(binary => new LlmAttachment
                {
                    Kind = binary.Kind == LlmBinaryKind.Image ? LlmBinaryKind.Image : LlmBinaryKind.Document,
                    FileName = binary.FileName ?? "attachment",
                    Data = binary.Data
                })];
        int uncarried = toolBytes.Length - toolBytes.OfType<LlmToolOutputBinary>()
            .Count(binary => binary.Kind is LlmBinaryKind.Image or LlmBinaryKind.Document);
        if (carriesAttachments)
        {
            LlmAttachments.Validate(attachments, supportsImages: true, supportsDocuments: true, parameterName: nameof(options));
        }
        string notice = "History was carried as opening context in a new provider conversation; "
            + $"continuation state from {omitted} earlier messages was not carried.";
        int notCarried = uncarried + (carriesAttachments ? 0 : attachments.Length);
        if (notCarried > 0)
        {
            notice += $" {notCarried} history attachments could not be carried.";
        }
        return new(text.ToString(), carriesAttachments ? attachments : [], notice);
    }
}
