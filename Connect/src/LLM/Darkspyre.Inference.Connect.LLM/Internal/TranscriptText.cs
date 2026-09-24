using System.Text;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// The text form of a history, for providers that take one prompt: each message under its role header, with
/// tool calls and tool results as labelled blocks. The CLI chat clients and agent opening context share it.
/// </summary>
internal static class TranscriptText
{
    public static void Append(StringBuilder text, IEnumerable<LlmChatMessage> messages)
    {
        foreach (LlmChatMessage message in messages)
        {
            text.Append('[').Append(message.Role.Trim().ToUpperInvariant()).AppendLine("]");
            text.AppendLine(message.Content);
            foreach (LlmToolCall call in message.ToolCalls)
            {
                text.Append("[TOOL CALL id=").Append(call.Id).Append(" name=").Append(call.Name).AppendLine("]");
                text.AppendLine(call.ArgumentsJson);
            }
            foreach (LlmToolResult result in message.ToolResults)
            {
                text.Append("[TOOL RESULT id=").Append(result.ToolCallId)
                    .Append(" status=").Append(result.IsError ? "error" : "ok").AppendLine("]");
                text.AppendLine(result.Content);
            }
            string[] attachments = [.. message.Attachments
                .Concat(message.ToolResults.SelectMany(result => result.Attachments))
                .Select(attachment => attachment.FileName)];
            if (attachments.Length > 0)
            {
                text.Append("[ATTACHMENTS: ").Append(string.Join(", ", attachments)).AppendLine("]");
            }
        }
    }
}
