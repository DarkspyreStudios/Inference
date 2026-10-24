using System.Text;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// The text form of a history, for providers that take one prompt: each message under its role header, with
/// tool calls and tool results as labelled blocks. The CLI chat clients and agent opening context share it. A tool call
/// is written under the name <c>toolName</c> gives it, so a provider that offers the model its tools under other names
/// writes the history under those names; without <c>toolName</c> a call keeps its own name.
/// </summary>
internal static class TranscriptText
{
    // The header carries each result's status, so the error flag needs no text marker of its own.
    private static readonly LlmRichContentSupported Support =
        RichContentRoutes.TextOnly with { ErrorFlag = LlmRichContentCarriage.Native };

    public static void Append(StringBuilder text, IEnumerable<LlmChatMessage> messages,
        Func<LlmToolOutput, ProjectedToolOutput>? project = null, Func<string, string>? toolName = null)
    {
        foreach (LlmChatMessage message in messages)
        {
            text.Append('[').Append(message.Role.Trim().ToUpperInvariant()).AppendLine("]");
            text.AppendLine(message.Content);
            foreach (LlmToolCall call in ClientTools.Functions(message.ToolCalls))
            {
                text.Append("[TOOL CALL id=").Append(call.Id).Append(" name=").Append(toolName is null ? call.Name : toolName(call.Name)).AppendLine("]");
                text.AppendLine(call.ArgumentsJson);
            }
            var omittedNames = new List<string>();
            foreach (LlmToolResult result in ClientTools.Functions(message.ToolResults))
            {
                ProjectedToolOutput projected = project is null
                    ? ToolOutputProjector.Project(result.Output, Support, Support) : project(result.Output);
                text.Append("[TOOL RESULT id=").Append(result.ToolCallId)
                    .Append(" status=").Append(result.Output.IsError ? "error" : "ok").AppendLine("]");
                text.AppendLine(projected.Text);
                for (int index = 0; index < result.Output.Parts.Count; index++)
                {
                    if (projected.Report.Parts[index].Delivery == LlmToolPartDelivery.Omitted
                        && result.Output.Parts[index] is { } part && NameOf(part) is { } name)
                    {
                        omittedNames.Add(name);
                    }
                }
            }
            string[] attachments = [.. message.Attachments.Select(attachment => LlmAttachments.RequireBinary(attachment).FileName), .. omittedNames];
            if (attachments.Length > 0)
            {
                text.Append("[ATTACHMENTS: ").Append(string.Join(", ", attachments)).AppendLine("]");
            }
        }
    }

    /// <summary>The name a transcript lists for a part it cannot carry, or null for a part without bytes.</summary>
    private static string? NameOf(LlmToolOutputPart part) => part switch
    {
        LlmToolOutputBinary binary => binary.FileName ?? binary.MediaType,
        LlmToolOutputResource { Data: not null } resource => resource.Name ?? resource.Uri,
        _ => null
    };
}
