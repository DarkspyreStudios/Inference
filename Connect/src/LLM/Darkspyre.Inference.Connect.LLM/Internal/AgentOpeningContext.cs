using System.Text;

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
        if (options.UnboundResponseState != LlmUnboundResponseState.Omit)
        {
            throw new NotSupportedException(
                "This runtime starts a new provider conversation that carries no response state. "
                + "Start with UnboundResponseState.Omit to carry the context as opening context.");
        }
        (LlmAgentContext context, int omitted) = ManagedAgentContext.WithoutResponseState(options.Context);
        if (context.Entries.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        text.AppendLine(Header);
        TranscriptText.Append(text, context.Entries.Select(entry => entry.Message));
        LlmAttachment[] attachments = [.. context.Entries.SelectMany(entry => entry.Message.Attachments
            .Concat(entry.Message.ToolResults.SelectMany(result => result.Attachments)))];
        if (carriesAttachments)
        {
            LlmAttachments.Validate(attachments, supportsImages: true, supportsDocuments: true, parameterName: nameof(options));
        }
        string notice = "History was carried as opening context in a new provider conversation; "
            + $"response state from {omitted} earlier messages was not carried.";
        if (!carriesAttachments && attachments.Length > 0)
        {
            notice += $" {attachments.Length} history attachments could not be carried.";
        }
        return new(text.ToString(), carriesAttachments ? attachments : [], notice);
    }
}
