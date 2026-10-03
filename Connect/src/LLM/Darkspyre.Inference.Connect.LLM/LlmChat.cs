using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Connect.LLM;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmChatMessage(string Role, string Content)
{
    /// <summary>Complete adapter-owned output, serialized instead of visible assistant text/calls.</summary>
    public LlmContinuationState? ContinuationState { get; init; }

    public IReadOnlyList<LlmOmittedAttachment> OmittedAttachments { get; init; } = [];

    /// <summary>Ordered inline content, provider files or workspace resources supplied with this message.</summary>
    public IReadOnlyList<LlmMessageAttachment> Attachments { get; init; } = [];

    /// <summary>Tool calls previously requested by the assistant.</summary>
    public IReadOnlyList<LlmClientToolCall> ToolCalls { get; init; } = [];

    /// <summary>Host-executed results for previous tool calls.</summary>
    public IReadOnlyList<LlmClientToolResult> ToolResults { get; init; } = [];
}

/// <summary>Provider-neutral generation request. The model is selected per request.</summary>
public sealed record LlmChatRequest
{
    /// <summary>Borrowed caller-owned resource authority. Never part of a portable request.</summary>
    public IInferenceResourceAccess? Resources { get; init; }

    public override string ToString() => nameof(LlmChatRequest);

    public required string ModelId { get; init; }

    public string? Instructions { get; init; }

    public IReadOnlyList<LlmChatMessage> Messages { get; init; } = [];

    public double? Temperature { get; init; }

    public int? MaxOutputTokens { get; init; }

    /// <summary>Nucleus sampling in (0, 1]. Null leaves the provider default.</summary>
    public double? TopP { get; init; }

    /// <summary>Sample from the K most likely tokens; positive. Null leaves the provider default.</summary>
    public int? TopK { get; init; }

    /// <summary>A non-negative sampling seed. Determinism is not guaranteed.</summary>
    public int? Seed { get; init; }

    /// <summary>Penalty for a token already present in the generated response.</summary>
    public double? PresencePenalty { get; init; }

    /// <summary>Penalty multiplied by a token's occurrences in the generated response.</summary>
    public double? FrequencyPenalty { get; init; }

    /// <summary>Stop before any of these non-empty strings. Empty requests no string stops.</summary>
    public IReadOnlyList<string> StopSequences { get; init; } = [];

    /// <summary>The requested reasoning level; null leaves the provider's default in place.</summary>
    public Darkspyre.Inference.Common.LLM.LlmReasoningLevel? ReasoningLevel { get; init; }

    public LlmStructuredOutputRequest? StructuredOutput { get; init; }

    public IReadOnlyList<LlmClientToolDefinition> Tools { get; init; } = [];

    public IReadOnlyList<LlmHostedTool> HostedTools { get; init; } = [];

    public int? MaxHostedTurns { get; init; }

    public LlmToolChoice? ToolChoice { get; init; }

    /// <summary>Requests or disables parallel tool calls when the provider supports that control.</summary>
    public bool? AllowParallelToolCalls { get; init; }

    /// <summary>
    /// Optional stable conversation affinity key. This is neither storage nor provider resume state.
    /// </summary>
    public string? ConversationKey { get; init; }

    public LlmPromptCacheHint? PromptCache { get; init; }

    /// <summary>An explicitly selected native cache, independently of advisory prompt-cache hints.</summary>
    public LlmExplicitCacheReference? ExplicitCache { get; init; }

    /// <summary>Typed provider controls. Null inherits the selected connection default.</summary>
    public LlmProviderControls? ProviderControls { get; init; }

    /// <summary>Per-operation provider JSON properties; standard request fields cannot be replaced.</summary>
    public IReadOnlyDictionary<string, JsonElement> ProviderOptions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>
    /// Set to <see cref="LlmUnboundContinuationState.Omit"/> when carrying retained history to a new conversation
    /// on another model or provider.
    /// </summary>
    public LlmUnboundContinuationState UnboundContinuationState { get; init; } = LlmUnboundContinuationState.Reject;
}

public sealed record LlmProviderDiagnostics(
    int HttpStatusCode,
    string? RequestId,
    string? RawFinalResponseJson)
{
    /// <summary>The applied service tier when the provider reports one.</summary>
    public string? ServiceTier { get; init; }
}

public sealed record LlmChatResult
{
    public required string Text { get; init; }

    /// <summary>The actual model identity reported by the adapter; null when unknown.</summary>
    public string? ModelId { get; init; }

    /// <summary>A separately observed model revision; null when unknown.</summary>
    public string? ModelRevision { get; init; }

    public string? Refusal { get; init; }

    /// <summary>Provider refusal category when the provider names one.</summary>
    public string? RefusalCategory { get; init; }

    public required LlmCompletionStatus Status { get; init; }

    public string? FinishReason { get; init; }

    public string? IncompleteReason { get; init; }

    public LlmUsage? Usage { get; init; }

    public string? ProviderResponseId { get; init; }

    public IReadOnlyList<LlmClientToolCall> ToolCalls { get; init; } = [];

    public IReadOnlyList<LlmCitation> Citations { get; init; } = [];

    /// <summary>Preserve unchanged on this response's assistant message, including ordinary turns.</summary>
    public LlmContinuationState? ContinuationState { get; init; }

    public LlmStructuredOutputValidation? StructuredOutput { get; init; }

    public required LlmProviderDiagnostics Diagnostics { get; init; }

    /// <summary>How many request messages were sent without their continuation state.</summary>
    public int OmittedContinuationStateCount { get; init; }
}
