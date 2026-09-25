using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM;

public sealed record LlmChatMessage(string Role, string Content)
{
    /// <summary>Complete adapter-owned output, serialized instead of visible assistant text/calls.</summary>
    public LlmProviderResponseState? ResponseState { get; init; }

    public IReadOnlyList<LlmOmittedAttachment> OmittedAttachments { get; init; } = [];

    /// <summary>Provider-neutral binary content supplied with this user message.</summary>
    public IReadOnlyList<LlmAttachment> Attachments { get; init; } = [];

    /// <summary>Tool calls previously requested by the assistant.</summary>
    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];

    /// <summary>Host-executed results for previous tool calls.</summary>
    public IReadOnlyList<LlmToolResult> ToolResults { get; init; } = [];
}

/// <summary>A host-provided tool that the model may request.</summary>
public sealed record LlmToolDefinition
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required JsonElement InputSchema { get; init; }

    /// <summary>
    /// Requests provider-enforced schema adherence. The schema must then meet the provider's strict
    /// rules; for OpenAI that means every object closes <c>additionalProperties</c> and lists every
    /// property as required. A host schema describes what the tool accepts, so the default is false.
    /// </summary>
    public bool Strict { get; init; }
}

public enum LlmToolChoiceMode
{
    Auto,
    None,
    Required,
    Specific
}

/// <summary>
/// What happens to message response state that the target transport, model or account cannot replay.
/// </summary>
public enum LlmUnboundResponseState
{
    /// <summary>The request fails before any provider call.</summary>
    Reject,

    /// <summary>
    /// The sent request carries the message without its state; visible content, tool calls, tool results
    /// and attachments are kept. The caller's messages are not modified.
    /// </summary>
    Omit
}

/// <summary>Controls whether, and which, tool the model may request.</summary>
public sealed record LlmToolChoice
{
    public LlmToolChoiceMode Mode { get; init; } = LlmToolChoiceMode.Auto;

    /// <summary>Required only when <see cref="Mode"/> is <see cref="LlmToolChoiceMode.Specific"/>.</summary>
    public string? Name { get; init; }
}

/// <summary>A tool invocation requested by the model. Arguments are preserved as provider JSON.</summary>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>The high-level kind of binary content supplied to a model.</summary>
public enum LlmAttachmentKind
{
    Image,
    Document
}

/// <summary>
/// Provider-neutral binary content supplied with a prompt or host tool result. Adapters own media
/// detection and any base64, data-URL, upload, staging, or provider content-block representation.
/// </summary>
public sealed record LlmAttachment
{
    public required LlmAttachmentKind Kind { get; init; }

    public required string FileName { get; init; }

    public required ReadOnlyMemory<byte> Data { get; init; }
}

/// <summary>The output of a tool invocation executed by the host application.</summary>
public sealed record LlmToolResult(string ToolCallId, LlmToolOutput Output);

/// <summary>How far a structured request may fall back when native JSON Schema is unavailable.</summary>
public enum LlmStructuredOutputFallback
{
    None,
    JsonObject,
    LocalValidation
}

public enum LlmStructuredOutputEnforcement
{
    ProviderJsonSchema,
    ProviderJsonObject,
    LocalValidationOnly
}

public sealed record LlmStructuredOutputRequest
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required JsonElement Schema { get; init; }

    public bool Strict { get; init; } = true;

    public LlmStructuredOutputFallback Fallback { get; init; } = LlmStructuredOutputFallback.None;
}

/// <summary>Provider-neutral generation request. The model is selected per request.</summary>
public sealed record LlmChatRequest
{
    public required string ModelId { get; init; }

    public string? Instructions { get; init; }

    public IReadOnlyList<LlmChatMessage> Messages { get; init; } = [];

    public double? Temperature { get; init; }

    public int? MaxOutputTokens { get; init; }

    /// <summary>The requested reasoning level; null leaves the provider's default in place.</summary>
    public Darkspyre.Inference.Common.LLM.LlmReasoningLevel? ReasoningLevel { get; init; }

    public LlmStructuredOutputRequest? StructuredOutput { get; init; }

    public IReadOnlyList<LlmToolDefinition> Tools { get; init; } = [];

    public LlmToolChoice? ToolChoice { get; init; }

    /// <summary>Requests or disables parallel tool calls when the provider supports that control.</summary>
    public bool? AllowParallelToolCalls { get; init; }

    /// <summary>
    /// Optional stable conversation affinity key. This is neither storage nor provider resume state.
    /// </summary>
    public string? ConversationKey { get; init; }

    /// <summary>Per-operation provider JSON properties; standard request fields cannot be replaced.</summary>
    public IReadOnlyDictionary<string, JsonElement> ProviderOptions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>
    /// Set to <see cref="LlmUnboundResponseState.Omit"/> when carrying retained history to a new conversation
    /// on another model or provider.
    /// </summary>
    public LlmUnboundResponseState UnboundResponseState { get; init; } = LlmUnboundResponseState.Reject;
}

public sealed record LlmChatUsage(int? InputTokens, int? OutputTokens, int? TotalTokens);

public enum LlmCompletionStatus
{
    Completed,
    Incomplete,
    Refused,
    Failed,
    RequiresAction
}

public sealed record LlmStructuredOutputValidation(
    LlmStructuredOutputEnforcement Enforcement,
    bool WasEvaluated,
    bool IsValid,
    IReadOnlyList<string> Errors);

public sealed record LlmProviderDiagnostics(
    int HttpStatusCode,
    string? RequestId,
    string? RawFinalResponseJson);

public sealed record LlmChatResult
{
    public required string Text { get; init; }

    public string? Refusal { get; init; }

    /// <summary>Provider refusal category when the provider names one.</summary>
    public string? RefusalCategory { get; init; }

    public required LlmCompletionStatus Status { get; init; }

    public string? FinishReason { get; init; }

    public string? IncompleteReason { get; init; }

    public LlmChatUsage? Usage { get; init; }

    public string? ProviderResponseId { get; init; }

    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];

    /// <summary>Preserve unchanged on this response's assistant message, including ordinary turns.</summary>
    public LlmProviderResponseState? ResponseState { get; init; }

    public LlmStructuredOutputValidation? StructuredOutput { get; init; }

    public required LlmProviderDiagnostics Diagnostics { get; init; }

    /// <summary>How many request messages were sent without their response state.</summary>
    public int OmittedResponseStateCount { get; init; }
}
