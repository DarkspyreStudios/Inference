using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Reasoning text a complete response carried, in response order.</summary>
internal sealed record LlmReasoningText(string Text, bool IsSummary);

/// <summary>
/// The events of a response that arrived complete, in a fixed order: reasoning, text, refusal, each tool call, then
/// the completion. The completion carries the same result a streamed response of the same content produces.
/// </summary>
internal static class CompleteResponseEvents
{
    public static IEnumerable<LlmStreamEvent> From(LlmChatResult result, IReadOnlyList<LlmReasoningText> reasoning)
    {
        foreach (LlmReasoningText entry in reasoning)
        {
            if (entry.Text.Length > 0)
            {
                yield return new LlmReasoningDeltaEvent(entry.Text, entry.IsSummary);
            }
        }

        if (!string.IsNullOrEmpty(result.Text))
        {
            yield return new LlmTextDeltaEvent(result.Text);
        }

        // A provider that reports refused text as both text and refusal emits it once, as text.
        if (!string.IsNullOrEmpty(result.Refusal) && !string.Equals(result.Refusal, result.Text, StringComparison.Ordinal))
        {
            yield return new LlmRefusalDeltaEvent(result.Refusal);
        }

        for (int index = 0; index < result.ToolCalls.Count; index++)
        {
            if (result.ToolCalls[index] is LlmShellCall shell)
            {
                yield return new LlmShellCallEvent((LlmShellCall)ClientTools.Snapshot(shell));
                continue;
            }
            LlmToolCall call = result.ToolCalls[index] as LlmToolCall
                ?? throw new NotSupportedException("Unknown client tool call.");
            yield return new LlmToolCallStartedEvent(call.Id, call.Name, index);
            if (call.ArgumentsJson.Length > 0)
            {
                yield return new LlmToolCallArgumentsDeltaEvent(call.Id, call.ArgumentsJson);
            }
            yield return new LlmToolCallCompletedEvent(call);
        }

        yield return new LlmChatCompletionEvent(result);
    }
}
