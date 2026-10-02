using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Single snapshot validation/copy boundary, used for import, capture and replacement.</summary>
internal static class ManagedAgentContext
{
    /// <summary>Validates portable data without decoding provider state or invoking resource authority.</summary>
    internal static LlmAgentContext ValidatePortable(LlmAgentContext context) => Validate(context, static message => message);

    public static LlmAgentContext Import(LlmAgentContext context, LlmConnection connection, string modelId)
    {
        ValidateRuntimeResources(context);
        return Validate(context, message =>
        {
            _ = ProviderContinuationStateCodec.DecodeMessage(message, connection, modelId);
            return message;
        });
    }

    /// <summary>Imports a start context; with Omit, state this target cannot replay is removed and counted.</summary>
    public static (LlmAgentContext Context, int Omitted) Import(
        LlmAgentContext context, LlmConnection connection, string modelId, LlmUnboundContinuationState unbound)
    {
        if (!Enum.IsDefined(unbound))
        {
            throw new ArgumentOutOfRangeException(nameof(unbound), "Unknown unbound continuation state handling.");
        }
        if (unbound == LlmUnboundContinuationState.Reject)
        {
            return (Import(context, connection, modelId), 0);
        }
        ValidateRuntimeResources(context);
        int omitted = 0;
        LlmAgentContext imported = Validate(context, message =>
        {
            if (ProviderContinuationStateCodec.IsBound(message.ContinuationState!, connection, modelId))
            {
                _ = ProviderContinuationStateCodec.DecodeMessage(message, connection, modelId);
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
    public static (LlmAgentContext Context, int Omitted) WithoutContinuationState(LlmAgentContext context)
    {
        ValidateRuntimeResources(context);
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
        if (context.SelectedResources.Count > 0 || context.ResourceSelectionVersion is not null)
            throw new NotSupportedException("This runtime does not bind caller-owned resource selections.");
        LlmAttachments.ValidateReferences(new LlmChatRequest
        {
            ModelId = string.Empty,
            Messages = context.Entries.Select(entry => entry.Message).ToArray()
        });
    }

    /// <summary>
    /// One structural pass over a context. <paramref name="continuationState"/> validates or rewrites each message
    /// that carries continuation state.
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
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Dictionary<string, LlmClientToolCall>(StringComparer.Ordinal);
        var entries = new List<LlmAgentContextEntry>(context.Entries.Count);
        foreach (var entry in context.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Message);
            if (string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id) || string.IsNullOrWhiteSpace(entry.TurnId))
            {
                throw new ArgumentException("Context entries require unique IDs and a turn identity.");
            }
            var message = entry.Message;
            ArgumentNullException.ThrowIfNull(message.Attachments);
            ArgumentNullException.ThrowIfNull(message.ToolCalls);
            ArgumentNullException.ThrowIfNull(message.ToolResults);
            ArgumentNullException.ThrowIfNull(message.OmittedAttachments);
            if (message.Role is not ("user" or "assistant" or "system" or "developer")
                || message.Content is null || (message.ToolCalls.Count > 0 && message.Role != "assistant")
                || (message.ToolResults.Count > 0 && message.Role != "user")
                || (pending.Count > 0 && message.ToolResults.Count == 0))
            {
                throw new ArgumentException("Context roles and complete tool groups must remain ordered.");
            }
            if (message.ContinuationState is not null)
            {
                message = continuationState(message);
            }
            entries.Add(entry with { Message = message });
            foreach (var call in message.ToolCalls)
            {
                ArgumentNullException.ThrowIfNull(call);
                if (string.IsNullOrWhiteSpace(call.Id) || !calls.Add(call.Id))
                {
                    throw new ArgumentException("Context tool-call IDs must be unique and names nonempty.");
                }
                if (call is LlmToolCall function)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(function.Name);
                    using var arguments = JsonDocument.Parse(function.ArgumentsJson);
                    if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("Context tool arguments must be JSON objects.");
                }
                else if (call is not LlmShellCall) throw new NotSupportedException("Unknown context tool call.");
                pending.Add(call.Id, call);
            }
            foreach (var result in message.ToolResults)
            {
                ArgumentNullException.ThrowIfNull(result);
                if (!pending.Remove(result.ToolCallId, out LlmClientToolCall? call)
                    || (call is LlmToolCall && result is not LlmToolResult)
                    || (call is LlmShellCall && result is not LlmShellCallResult))
                {
                    throw new ArgumentException("Each tool result must match exactly one preceding call of the same kind.");
                }
                if (result is LlmToolResult function)
                {
                    ArgumentNullException.ThrowIfNull(function.Output);
                    ArgumentNullException.ThrowIfNull(function.Output.Parts);
                    foreach (var part in function.Output.Parts) ArgumentNullException.ThrowIfNull(part);
                }
                else if (result is not LlmShellCallResult) throw new NotSupportedException("Unknown context tool result.");
            }
            var omissions = new HashSet<(string?, int)>();
            foreach (var omitted in message.OmittedAttachments)
            {
                ArgumentNullException.ThrowIfNull(omitted);
                if (!Enum.IsDefined(omitted.Kind) || string.IsNullOrWhiteSpace(omitted.FileName)
                    || omitted.Index < 0 || !omissions.Add((omitted.ToolCallId, omitted.Index))
                    || (omitted.ToolCallId is not null && !message.ToolResults.Any(result => result is LlmToolResult
                        && result.ToolCallId == omitted.ToolCallId)))
                {
                    throw new ArgumentException("Invalid omitted-attachment descriptor.");
                }
            }
            foreach (var group in message.OmittedAttachments.GroupBy(omitted => omitted.ToolCallId))
            {
                int retained = group.Key is null ? message.Attachments.Count
                    : ((LlmToolResult)message.ToolResults.Single(result => result.ToolCallId == group.Key)).Output.Parts.Count;
                long conceptualCount = (long)retained + group.Count();
                if (group.Any(omitted => omitted.Index >= conceptualCount))
                    throw new ArgumentException("Omitted-attachment positions must refer to the original ordered content.");
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

    public static bool Equivalent(LlmAgentContextEntry left, LlmAgentContextEntry right) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left, ConnectLlmJson.AgentContextEntry),
            JsonSerializer.SerializeToElement(right, ConnectLlmJson.AgentContextEntry));
}
