using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Single snapshot validation/copy boundary, used for import, capture and replacement.</summary>
internal static class ManagedAgentContext
{
    /// <summary>Validates portable data without decoding provider state or invoking resource authority.</summary>
    internal static LlmAgentContext ValidatePortable(LlmAgentContext context) =>
        Validate(context, static message => message);

    /// <summary>
    /// Imports a context for the selected model. State this target cannot replay is removed and counted, so a history
    /// from any provider, model or account continues.
    /// </summary>
    public static (LlmAgentContext Context, int Omitted) Import(
        LlmAgentContext context, LlmConnection connection, string modelId, ILlmContinuationCodec? codec,
        bool supportsResources = false)
    {
        if (!supportsResources) ValidateRuntimeResources(context);
        int omitted = 0;
        LlmAgentContext imported = Validate(context, message =>
        {
            if (ProviderContinuationStateCodec.IsReplayable(message, connection, modelId, codec))
            {
                return message;
            }
            omitted++;
            return message with { ContinuationState = null };
        });
        return (imported, omitted);
    }

    /// <summary>
    /// Validates a context for a new provider-side conversation, which carries no continuation state. Every
    /// entry's state is removed and counted.
    /// </summary>
    public static (LlmAgentContext Context, int Omitted) WithoutContinuationState(LlmAgentContext context, bool supportsResources = false)
    {
        if (!supportsResources) ValidateRuntimeResources(context);
        int omitted = 0;
        LlmAgentContext validated = Validate(context, message =>
        {
            omitted++;
            return message with { ContinuationState = null };
        });
        return (validated, omitted);
    }

    internal static void ValidateRuntimeResources(LlmAgentContext context)
    {
        context = ValidatePortable(context);
        LlmAttachments.ValidateReferences(new LlmChatRequest
        {
            ModelId = string.Empty,
            Messages = context.Entries.Select(entry => entry.Message).ToArray()
        });
    }

    /// <summary>
    /// One pass over a context. <paramref name="continuationState"/> validates or rewrites each message that carries
    /// continuation state. Structure is not refused here: <see cref="Outgoing"/> repairs the copy that is sent.
    /// </summary>
    private static LlmAgentContext Validate(LlmAgentContext context, Func<LlmChatMessage, LlmChatMessage> continuationState)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ValidateResourceSelection();
        ArgumentNullException.ThrowIfNull(context.Entries);
        if (context.FormatVersion != 3 || context.Revision < 0)
        {
            throw new ArgumentException("Unsupported context format or revision.");
        }
        var entries = new List<LlmAgentContextEntry>(context.Entries.Count);
        foreach (var entry in context.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Message);
            var message = entry.Message;
            ArgumentNullException.ThrowIfNull(message.Content);
            ArgumentNullException.ThrowIfNull(message.Attachments);
            ArgumentNullException.ThrowIfNull(message.ToolCalls);
            ArgumentNullException.ThrowIfNull(message.ToolResults);
            ArgumentNullException.ThrowIfNull(message.OmittedAttachments);
            if (message.ContinuationState is not null)
            {
                message = continuationState(message);
            }
            entries.Add(entry with { Message = message });
            foreach (var call in message.ToolCalls)
            {
                ArgumentNullException.ThrowIfNull(call);
                if (call is LlmToolCall function)
                {
                    // Stored arguments may be any string: they are what the model sent.
                    ArgumentException.ThrowIfNullOrWhiteSpace(function.Name);
                    ArgumentNullException.ThrowIfNull(function.ArgumentsJson);
                }
                else if (call is not LlmShellCall) throw new NotSupportedException("Unknown context tool call.");
            }
            foreach (var result in message.ToolResults)
            {
                ArgumentNullException.ThrowIfNull(result);
                if (result is LlmToolResult function)
                {
                    ArgumentNullException.ThrowIfNull(function.Output);
                    ArgumentNullException.ThrowIfNull(function.Output.Parts);
                    foreach (var part in function.Output.Parts) ArgumentNullException.ThrowIfNull(part);
                }
                else if (result is not LlmShellCallResult) throw new NotSupportedException("Unknown context tool result.");
            }
            foreach (var omitted in message.OmittedAttachments) ArgumentNullException.ThrowIfNull(omitted);
        }
        return Copy(context with { Entries = entries }, includeAttachmentData: true);
    }

    /// <summary>The result an unsettled tool call receives in the request that is sent.</summary>
    internal const string UnsettledCallResult = "The tool was not run; no result is available.";

    /// <summary>
    /// The messages a context sends, repaired; the context itself is unchanged. A duplicate entry id keeps the first
    /// entry, a duplicate call id keeps the first call, a result with no unsettled call of its kind is dropped, and roles
    /// keep their recorded order. Function calls still unsettled where their tool group ends get
    /// <see cref="UnsettledCallResult"/>. Omission descriptors are never sent.
    /// </summary>
    internal static IReadOnlyList<LlmChatMessage> Outgoing(IReadOnlyList<LlmAgentContextEntry> entries)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<LlmClientToolCall>();
        var messages = new List<LlmChatMessage>(entries.Count + 1);
        void Settle()
        {
            LlmToolResult[] unsettled = [.. pending.OfType<LlmToolCall>()
                .Select(call => new LlmToolResult(call.Id, LlmToolOutput.FromText(UnsettledCallResult, isError: true)))];
            if (unsettled.Length > 0) messages.Add(new("user", "") { ToolResults = unsettled });
            pending.RemoveAll(call => call is LlmToolCall);
        }
        foreach (LlmAgentContextEntry entry in entries)
        {
            if (!ids.Add(entry.Id)) continue;
            LlmChatMessage message = entry.Message;
            // A message that settles nothing ends the tool group before it.
            if (message.ToolResults.Count == 0) Settle();
            var results = new List<LlmClientToolResult>(message.ToolResults.Count);
            foreach (LlmClientToolResult result in message.ToolResults)
            {
                int index = pending.FindIndex(call => call.Id == result.ToolCallId
                    && (call is LlmToolCall ? result is LlmToolResult : result is LlmShellCallResult));
                if (index < 0) continue;
                pending.RemoveAt(index);
                results.Add(result);
            }
            LlmClientToolCall[] sentCalls = [.. message.ToolCalls.Where(call => calls.Add(call.Id))];
            pending.AddRange(sentCalls);
            // A message that carried only dropped results has nothing left to send.
            if (message.ToolResults.Count > 0 && results.Count == 0 && sentCalls.Length == 0 && message.Content.Length == 0
                && message.Attachments.Count == 0 && message.ContinuationState is null) continue;
            messages.Add(message with { ToolCalls = sentCalls, ToolResults = results, OmittedAttachments = [] });
        }
        Settle();
        return messages;
    }

    public static LlmAgentContext Copy(LlmAgentContext context, bool includeAttachmentData) => context with
    {
        SelectedResources = context.SelectedResources,
        ResourceSelectionVersion = context.ResourceSelectionVersion,
        Entries = Array.AsReadOnly(context.Entries.Select(entry => entry with
        {
            Message = CopyMessage(entry.Message, includeAttachmentData)
        }).ToArray())
    };

    public static LlmChatMessage CopyMessage(LlmChatMessage message, bool includeAttachmentData = true)
    {
        var omitted = message.OmittedAttachments.ToList();
        IReadOnlyList<LlmMessageAttachment> CopyAttachments(IReadOnlyList<LlmMessageAttachment> attachments, string? toolId)
        {
            if (includeAttachmentData)
            {
                return Array.AsReadOnly(attachments.Select(LlmAttachments.Copy).ToArray());
            }
            var retained = new List<LlmMessageAttachment>();
            int index = 0;
            for (int i = 0; i < attachments.Count; i++)
            {
                while (omitted.Any(item => item.ToolCallId == toolId && item.Index == index)) { index++; }
                if (attachments[i] is LlmAttachment binary)
                    omitted.Add(new(binary.Kind, binary.FileName, index, toolId));
                else
                    retained.Add(LlmAttachments.Copy(attachments[i]));
                index++;
            }
            return Array.AsReadOnly(retained.ToArray());
        }
        // Without attachment data, byte-bearing parts leave the output and are recorded by their position.
        LlmToolOutput StripOutput(LlmToolOutput output, string toolCallId)
        {
            var parts = new List<LlmToolOutputPart>(output.Parts.Count);
            int index = 0;
            foreach (var current in output.Parts)
            {
                while (omitted.Any(item => item.ToolCallId == toolCallId && item.Index == index)) { index++; }
                switch (current)
                {
                    case LlmToolOutputBinary binary:
                        omitted.Add(new(binary.Kind, binary.FileName ?? "attachment", index, toolCallId));
                        break;
                    case LlmToolOutputResource { BodyKind: { } kind } resource:
                        omitted.Add(new(kind, resource.Name ?? resource.Uri, index, toolCallId));
                        break;
                    case LlmToolOutputSessionResource resource:
                        parts.Add(new LlmToolOutputSessionResource(LlmAttachments.CopyResource(resource.Resource)));
                        break;
                    case var part:
                        parts.Add(part);
                        break;
                }
                index++;
            }
            return output with { Parts = Array.AsReadOnly(parts.ToArray()), StructuredContent = output.StructuredContent?.Clone() };
        }
        var attachments = CopyAttachments(message.Attachments, null);
        var results = message.ToolResults.Select(result => result switch
        {
            LlmToolResult function => (LlmClientToolResult)(function with
            {
                Output = includeAttachmentData ? CopyOutput(function.Output) : StripOutput(function.Output, function.ToolCallId)
            }),
            LlmShellCallResult shell => ClientTools.Snapshot(shell),
            _ => throw new NotSupportedException("Unknown context tool result.")
        }).ToArray();
        return message with
        {
            Attachments = attachments,
            ToolCalls = Array.AsReadOnly(message.ToolCalls.Select(ClientTools.Snapshot).ToArray()),
            ToolResults = Array.AsReadOnly(results),
            OmittedAttachments = Array.AsReadOnly(omitted.ToArray())
        };
    }

    /// <summary>A copy that shares no byte buffer or JSON value with the caller's output.</summary>
    public static LlmToolOutput CopyOutput(LlmToolOutput output) => output with
    {
        Parts = Array.AsReadOnly(output.Parts.Select(part => part switch
        {
            LlmToolOutputBinary binary => binary with { Data = binary.Data.ToArray() },
            LlmToolOutputResource { Data: { } data } resource => resource with { Data = data.ToArray() },
            LlmToolOutputSessionResource resource => new LlmToolOutputSessionResource(LlmAttachments.CopyResource(resource.Resource)),
            _ => part
        }).ToArray()),
        StructuredContent = output.StructuredContent?.Clone()
    };
}
