using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Single snapshot validation/copy boundary, used for import, capture and replacement.</summary>
internal static class ManagedAgentContext
{
    public static LlmAgentContext Import(LlmAgentContext context, LlmConnection connection, string modelId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.FormatVersion != 1 || context.Revision < 0)
        {
            throw new ArgumentException("Unsupported context format or revision.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
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
                _ = ProviderResponseStateCodec.DecodeMessage(message, connection, modelId);
            }
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
        return Copy(context, includeAttachmentData: true);
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
                omitted.Add(new(attachments[i].Kind, attachments[i].FileName, index++, toolId));
            }
            return [];
        }
        var attachments = CopyAttachments(message.Attachments, null);
        var results = message.ToolResults.Select(result => result with
        {
            Attachments = CopyAttachments(result.Attachments, result.ToolCallId)
        }).ToArray();
        return message with
        {
            Attachments = attachments,
            ToolCalls = Array.AsReadOnly(message.ToolCalls.ToArray()),
            ToolResults = Array.AsReadOnly(results),
            OmittedAttachments = Array.AsReadOnly(omitted.ToArray())
        };
    }

    public static bool Equivalent(LlmAgentContextEntry left, LlmAgentContextEntry right) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left), JsonSerializer.SerializeToElement(right));
}
