using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// The one place a request's values meet the selected model. A value the model cannot use is mapped to one it can,
/// or dropped; a dropped setting is not reported. Content left out of a message is named in that message's text.
/// </summary>
internal static class LlmRequestNormalizer
{
    /// <summary>Maps or drops every value the selected model cannot use. Never throws for a value.</summary>
    internal static LlmChatRequest Normalize(LlmConnection connection, LlmChatRequest request,
        LlmProviderCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capabilities);
        LlmClientToolDefinition[] tools = [.. Tools(request.Tools)];
        return request with
        {
            ReasoningLevel = LlmReasoningEffort.Nearest(request.ReasoningLevel, capabilities.ReasoningLevels),
            Temperature = Usable(request.Temperature, capabilities.SupportsTemperature, double.IsFinite),
            TopP = Usable(request.TopP, capabilities.SupportsTopP, value => double.IsFinite(value) && value is > 0 and <= 1),
            TopK = Usable(request.TopK, capabilities.SupportsTopK, value => value > 0),
            Seed = Usable(request.Seed, capabilities.SupportsSeed, value => value >= 0),
            PresencePenalty = Usable(request.PresencePenalty, capabilities.SupportsPresencePenalty, double.IsFinite),
            FrequencyPenalty = Usable(request.FrequencyPenalty, capabilities.SupportsFrequencyPenalty, double.IsFinite),
            StopSequences = [.. request.StopSequences.Where(stop => !string.IsNullOrEmpty(stop))
                .Take(Math.Max(0, capabilities.MaxStopSequences))],
            MaxOutputTokens = request.MaxOutputTokens is > 0
                && capabilities.OutputLimitEnforcement != LlmOutputLimitEnforcement.Unsupported
                ? request.MaxOutputTokens : null,
            MaxHostedTurns = request.MaxHostedTurns is > 0 && capabilities.HostedTools.Count > 0 ? request.MaxHostedTurns : null,
            Tools = tools,
            ToolChoice = tools.Length == 0 ? null : Choice(request.ToolChoice, tools),
            AllowParallelToolCalls = tools.Length == 0 ? null : request.AllowParallelToolCalls,
            Messages = Messages(request.Messages, capabilities, request.Resources is not null)
        };
    }

    private static T? Usable<T>(T? value, InferenceSupport support, Func<T, bool> valid) where T : struct =>
        value is { } known && support != InferenceSupport.Unsupported && valid(known) ? known : null;

    /// <summary>The first definition of a duplicated name is the one sent.</summary>
    private static IEnumerable<LlmClientToolDefinition> Tools(IReadOnlyList<LlmClientToolDefinition> tools)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (LlmClientToolDefinition tool in tools)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (tool is not LlmToolDefinition function || names.Add(function.Name)) yield return tool;
        }
    }

    /// <summary>A Specific choice naming no request tool becomes Auto.</summary>
    private static LlmToolChoice? Choice(LlmToolChoice? choice, LlmClientToolDefinition[] tools) =>
        choice is { Mode: LlmToolChoiceMode.Specific }
        && !tools.OfType<LlmToolDefinition>().Any(tool => tool.Name == choice.Name)
            ? new LlmToolChoice { Mode = LlmToolChoiceMode.Auto }
            : choice;

    private static List<LlmChatMessage> Messages(IReadOnlyList<LlmChatMessage> messages,
        LlmProviderCapabilities capabilities, bool hasResourceAccess)
    {
        var sent = new List<LlmChatMessage>(messages.Count);
        // Attachments of non-user messages wait for the next user message.
        var carried = new List<(int Index, LlmMessageAttachment Attachment)>();
        for (int position = 0; position < messages.Count; position++)
        {
            LlmChatMessage message = messages[position];
            ArgumentNullException.ThrowIfNull(message);
            bool user = message.Role == "user";
            var notices = new List<string>();
            var attachments = new List<LlmMessageAttachment>();
            IEnumerable<(int Index, LlmMessageAttachment Attachment)> candidates =
                message.Attachments.Select((attachment, index) => (index, attachment));
            if (user)
            {
                candidates = [.. carried, .. candidates];
                carried.Clear();
            }
            foreach ((int index, LlmMessageAttachment attachment) in candidates)
            {
                if (!user)
                {
                    if (messages.Skip(position + 1).Any(next => next.Role == "user")) carried.Add((index, attachment));
                    else notices.Add(Notice(index, attachment, RouteOmits));
                    continue;
                }
                if (Omission(attachment, capabilities, hasResourceAccess) is { } reason) notices.Add(Notice(index, attachment, reason));
                else attachments.Add(attachment);
            }
            LlmChatMessage normalized = message with
            {
                Content = notices.Count == 0 ? message.Content : string.Join("\n", [.. message.Content.Length == 0 ? [] : new[] { message.Content }, .. notices]),
                Attachments = attachments
            };
            // A message holding both calls and results is sent as two messages, calls first.
            if (normalized.ToolCalls.Count > 0 && normalized.ToolResults.Count > 0)
            {
                sent.Add(normalized with { ToolResults = [] });
                sent.Add(new LlmChatMessage("user", "") { ToolResults = normalized.ToolResults });
            }
            else sent.Add(normalized);
        }
        return sent;
    }

    private const string ModelOmits = "The selected model does not support this attachment.";
    private const string RouteOmits = "The transport cannot carry this attachment.";

    /// <summary>Why an attachment is left out of the request that is sent, or null when it is sent.</summary>
    private static string? Omission(LlmMessageAttachment attachment, LlmProviderCapabilities capabilities, bool hasResourceAccess)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        bool images = capabilities.SupportsPromptImages != InferenceSupport.Unsupported;
        bool documents = capabilities.SupportsPromptDocuments != InferenceSupport.Unsupported;
        switch (attachment)
        {
            case LlmResourceAttachment:
                return !hasResourceAccess ? "No resource access was supplied." : documents ? null : ModelOmits;
            case LlmFileReference { File: { } file }:
                if (capabilities.SupportsFileReferences == InferenceSupport.Unsupported) return RouteOmits;
                return string.IsNullOrWhiteSpace(file.FileId) ? Invalid : documents ? null : ModelOmits;
            case LlmAttachment binary:
                if (binary.Kind is not (LlmBinaryKind.Image or LlmBinaryKind.Document)) return ModelOmits;
                if ((binary.Kind == LlmBinaryKind.Image && !images) || (binary.Kind == LlmBinaryKind.Document && !documents))
                    return ModelOmits;
                if (binary.Data.IsEmpty) return "The attachment is empty.";
                if (string.IsNullOrWhiteSpace(binary.FileName) || binary.FileName is "." or ".."
                    || binary.FileName.IndexOfAny(['/', '\\', '\0']) >= 0) return Invalid;
                try { _ = LlmAttachments.ResolveMediaType(binary); }
                catch (NotSupportedException) { return Invalid; }
                return null;
            default:
                return RouteOmits;
        }
    }

    private const string Invalid = "The attachment content is invalid.";

    private static string Notice(int index, LlmMessageAttachment attachment, string reason) =>
        $"[Attachment {index} ({(attachment as LlmAttachment)?.FileName ?? "attachment"}): {reason} This content was not supplied to the model.]";
}
