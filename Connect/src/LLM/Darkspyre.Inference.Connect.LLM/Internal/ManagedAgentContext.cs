using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Single snapshot validation/copy boundary, used for import, capture and replacement.</summary>
internal static class ManagedAgentContext
{
    public static LlmAgentContext Import(LlmAgentContext context, LlmConnection connection, string modelId) =>
        Validate(context, message =>
        {
            _ = ProviderResponseStateCodec.DecodeMessage(message, connection, modelId);
            return message;
        });

    /// <summary>Imports a start context; with Omit, state this target cannot replay is removed and counted.</summary>
    public static (LlmAgentContext Context, int Omitted) Import(
        LlmAgentContext context, LlmConnection connection, string modelId, LlmUnboundResponseState unbound)
    {
        if (!Enum.IsDefined(unbound))
        {
            throw new ArgumentOutOfRangeException(nameof(unbound), "Unknown unbound response state handling.");
        }
        if (unbound == LlmUnboundResponseState.Reject)
        {
            return (Import(context, connection, modelId), 0);
        }
        int omitted = 0;
        LlmAgentContext imported = Validate(context, message =>
        {
            if (ProviderResponseStateCodec.IsBound(message.ResponseState!, connection, modelId))
            {
                _ = ProviderResponseStateCodec.DecodeMessage(message, connection, modelId);
                return message;
            }
            omitted++;
            return message with { ResponseState = null };
        });
        return (imported, omitted);
    }

    /// <summary>
    /// Validates a context for a new provider-side conversation, which carries no response state. Every
    /// entry's state is removed and counted.
    /// </summary>
    public static (LlmAgentContext Context, int Omitted) WithoutResponseState(LlmAgentContext context)
    {
        int omitted = 0;
        LlmAgentContext validated = Validate(context, message =>
        {
            omitted++;
            return message with { ResponseState = null };
        });
        return (validated, omitted);
    }

    /// <summary>
    /// One structural pass over a context. <paramref name="responseState"/> validates or rewrites each message
    /// that carries response state.
    /// </summary>
    private static LlmAgentContext Validate(LlmAgentContext context, Func<LlmChatMessage, LlmChatMessage> responseState)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.FormatVersion != 1 || context.Revision < 0)
        {
            throw new ArgumentException("Unsupported context format or revision.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<LlmAgentContextEntry>(context.Entries.Count);
        foreach (var entry in context.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id) || string.IsNullOrWhiteSpace(entry.TurnId))
            {
                throw new ArgumentException("Context entries require unique IDs and a turn identity.");
            }
            var message = entry.Message;
            if (message.Role is not ("user" or "assistant" or "system" or "developer")
                || message.Content is null || (message.ToolCalls.Count > 0 && message.Role != "assistant")
                || (message.ToolResults.Count > 0 && message.Role != "user")
                || (pending.Count > 0 && message.ToolResults.Count == 0))
            {
                throw new ArgumentException("Context roles and complete tool groups must remain ordered.");
            }
            if (message.ResponseState is not null)
            {
                message = responseState(message);
            }
            entries.Add(entry with { Message = message });
            foreach (var call in message.ToolCalls)
            {
                if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name) || !calls.Add(call.Id))
                {
                    throw new ArgumentException("Context tool-call IDs must be unique and names nonempty.");
                }
                using var arguments = JsonDocument.Parse(call.ArgumentsJson);
                if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException("Context tool arguments must be JSON objects.");
                }
                pending.Add(call.Id);
            }
            foreach (var result in message.ToolResults)
            {
                if (!pending.Remove(result.ToolCallId))
                {
                    throw new ArgumentException("Each tool result must match exactly one preceding call.");
                }
            }
            var omissions = new HashSet<(string?, int)>();
            foreach (var omitted in message.OmittedAttachments)
            {
                if (!Enum.IsDefined(omitted.Kind) || string.IsNullOrWhiteSpace(omitted.FileName)
                    || omitted.Index < 0 || !omissions.Add((omitted.ToolCallId, omitted.Index))
                    || (omitted.ToolCallId is not null && !message.ToolResults.Any(result => result.ToolCallId == omitted.ToolCallId)))
                {
                    throw new ArgumentException("Invalid omitted-attachment descriptor.");
                }
            }
        }
        if (pending.Count > 0)
        {
            throw new ArgumentException("Context must end at a settled tool-group boundary.");
        }
        return Copy(context with { Entries = entries }, includeAttachmentData: true);
    }

    public static LlmAgentContext Copy(LlmAgentContext context, bool includeAttachmentData) => context with
    {
        Entries = Array.AsReadOnly(context.Entries.Select(entry => entry with
        {
            Message = CopyMessage(entry.Message, includeAttachmentData)
        }).ToArray())
    };

    public static LlmChatMessage CopyMessage(LlmChatMessage message, bool includeAttachmentData = true)
    {
        var omitted = message.OmittedAttachments.ToList();
        IReadOnlyList<LlmAttachment> CopyAttachments(IReadOnlyList<LlmAttachment> attachments, string? toolId)
        {
            if (includeAttachmentData)
            {
                return Array.AsReadOnly(attachments.Select(attachment => attachment with { Data = attachment.Data.ToArray() }).ToArray());
            }
            int index = 0;
            for (int i = 0; i < attachments.Count; i++)
            {
                while (omitted.Any(item => item.ToolCallId == toolId && item.Index == index)) { index++; }
                LlmBinaryKind kind = attachments[i].Kind == LlmAttachmentKind.Image ? LlmBinaryKind.Image : LlmBinaryKind.Document;
                omitted.Add(new(kind, attachments[i].FileName, index++, toolId));
            }
            return [];
        }
        // Without attachment data, byte-bearing parts leave the output and are recorded by their position.
        LlmToolOutput StripOutput(LlmToolOutput output, string toolCallId)
        {
            var parts = new List<LlmToolOutputPart>(output.Parts.Count);
            for (int index = 0; index < output.Parts.Count; index++)
            {
                switch (output.Parts[index])
                {
                    case LlmToolOutputBinary binary:
                        omitted.Add(new(binary.Kind, binary.FileName ?? "attachment", index, toolCallId));
                        break;
                    case LlmToolOutputResource { BodyKind: { } kind } resource:
                        omitted.Add(new(kind, resource.Name ?? resource.Uri, index, toolCallId));
                        break;
                    case var part:
                        parts.Add(part);
                        break;
                }
            }
            return output with { Parts = Array.AsReadOnly(parts.ToArray()), StructuredContent = output.StructuredContent?.Clone() };
        }
        var attachments = CopyAttachments(message.Attachments, null);
        var results = message.ToolResults.Select(result => result with
        {
            Output = includeAttachmentData ? CopyOutput(result.Output) : StripOutput(result.Output, result.ToolCallId)
        }).ToArray();
        return message with
        {
            Attachments = attachments,
            ToolCalls = Array.AsReadOnly(message.ToolCalls.ToArray()),
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
            _ => part
        }).ToArray()),
        StructuredContent = output.StructuredContent?.Clone()
    };

    public static bool Equivalent(LlmAgentContextEntry left, LlmAgentContextEntry right) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left), JsonSerializer.SerializeToElement(right));
}
