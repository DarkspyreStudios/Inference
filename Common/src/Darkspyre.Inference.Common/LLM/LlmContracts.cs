using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>One conversation message sent to a language model.</summary>
/// <param name="Role">The speaker role, such as user, assistant or system.</param>
/// <param name="Content">The message text.</param>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmMessage(string Role, string Content)
{
    /// <summary>Opaque continuation state from the implementation that produced this assistant message, kept unchanged with the message.</summary>
    public LlmContinuationState? ContinuationState { get; init; }

    /// <summary>Binary content and other attachments supplied with the message.</summary>
    public IReadOnlyList<LlmMessageAttachment> Attachments { get; init; } = [];

    /// <summary>Tool calls the assistant made in this message.</summary>
    public IReadOnlyList<LlmClientToolCall> ToolCalls { get; init; } = [];

    /// <summary>Results of client tool calls that this message returns to the model.</summary>
    public IReadOnlyList<LlmClientToolResult> ToolResults { get; init; } = [];
}

/// <summary>
/// Provider-neutral binary content supplied with a prompt. Adapters own any base64, data-URL, upload or
/// provider content-block representation. A set <see cref="MediaType"/> is used as given; otherwise the
/// adapter detects the type from the bytes.
/// </summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAttachment : LlmMessageAttachment
{
    /// <summary>The kind of binary content.</summary>
    public required LlmBinaryKind Kind { get; init; }

    /// <summary>The file name of the content.</summary>
    public required string FileName { get; init; }

    /// <summary>The media type of the content, or null to detect it from the bytes.</summary>
    public string? MediaType { get; init; }

    /// <summary>The content bytes.</summary>
    public required ReadOnlyMemory<byte> Data { get; init; }
}

/// <summary>A client tool the model may call, described by a name and a JSON input schema.</summary>
public sealed record LlmToolDefinition : LlmClientToolDefinition
{
    /// <summary>The tool name the model uses to call it.</summary>
    public required string Name { get; init; }

    /// <summary>A description of the tool for the model, when given.</summary>
    public string? Description { get; init; }

    /// <summary>The JSON schema of the tool's input.</summary>
    public required JsonElement InputSchema { get; init; }
}

/// <summary>How the model is allowed to choose tools.</summary>
public enum LlmToolChoiceMode
{
    /// <summary>The model decides whether to call a tool.</summary>
    Auto,
    /// <summary>The model must not call a tool.</summary>
    None,
    /// <summary>The model must call at least one tool.</summary>
    Required,
    /// <summary>The model must call the tool named in <see cref="LlmToolChoice.Name"/>.</summary>
    Specific
}

/// <summary>The tool-choice policy for a request.</summary>
public sealed record LlmToolChoice
{
    /// <summary>How the model is allowed to choose tools.</summary>
    public LlmToolChoiceMode Mode { get; init; } = LlmToolChoiceMode.Auto;

    /// <summary>The tool the model must call when <see cref="Mode"/> is <see cref="LlmToolChoiceMode.Specific"/>.</summary>
    public string? Name { get; init; }
}

/// <summary>A client tool call made by the model.</summary>
/// <param name="Id">The provider identifier of the call.</param>
/// <param name="Name">The name of the called tool.</param>
/// <param name="ArgumentsJson">The call arguments as a JSON string.</param>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson) : LlmClientToolCall(Id);

/// <summary>The output of a tool invocation executed by the host application.</summary>
/// <param name="Output">The tool's output.</param>
/// <param name="ToolCallId">The id of the tool call this result answers.</param>
public sealed record LlmToolResult(string ToolCallId, LlmToolOutput Output) : LlmClientToolResult(ToolCallId);

/// <summary>How structured output is enforced for a request.</summary>
public enum LlmStructuredOutputEnforcement
{
    /// <summary>The provider enforces the JSON schema.</summary>
    ProviderJsonSchema,
    /// <summary>The provider enforces JSON object output without the schema.</summary>
    ProviderJsonObject,
    /// <summary>The provider enforces nothing, and the output is validated locally against the schema.</summary>
    LocalValidationOnly
}

/// <summary>Asks the model to produce output that matches a JSON schema.</summary>
public sealed record LlmStructuredOutputRequest
{
    /// <summary>The name of the output schema.</summary>
    public required string Name { get; init; }

    /// <summary>A description of the expected output, when given.</summary>
    public string? Description { get; init; }

    /// <summary>The JSON schema the output must match.</summary>
    public required JsonElement Schema { get; init; }
}

/// <summary>Which part of the prompt a cache hint asks the provider to keep.</summary>
public enum LlmPromptCacheScope
{
    /// <summary>Tools and instructions.</summary>
    Prefix,

    /// <summary>Tools, instructions and the conversation through its last message.</summary>
    Conversation
}

/// <summary>How long a cached prefix should live.</summary>
public enum LlmPromptCacheRetention
{
    /// <summary>The provider's standard lifetime.</summary>
    Default,

    /// <summary>The provider's longer lifetime. Cache writes can cost more.</summary>
    Extended
}

/// <summary>
/// An advisory request to cache part of the prompt. Unsupported routes ignore it. Routes without the
/// requested retention use <see cref="LlmPromptCacheRetention.Default"/>.
/// </summary>
public sealed record LlmPromptCacheHint
{
    /// <summary>Which part of the prompt to cache.</summary>
    public LlmPromptCacheScope Scope { get; init; } = LlmPromptCacheScope.Conversation;

    /// <summary>How long the cached prefix lives.</summary>
    public LlmPromptCacheRetention Retention { get; init; }
}

/// <summary>How a route caches prompts. Hints do not guarantee a cache hit.</summary>
public sealed record LlmPromptCacheCapabilities
{
    /// <summary>The provider caches repeated prefixes without a hint.</summary>
    public InferenceSupport Automatic { get; init; }

    /// <summary>The route converts cache hints into provider cache markers.</summary>
    public InferenceSupport AcceptsHints { get; init; }

    /// <summary>Retentions the route honours for a hint. Empty when it accepts no hints.</summary>
    public IReadOnlySet<LlmPromptCacheRetention> Retentions { get; init; } = FrozenSet<LlmPromptCacheRetention>.Empty;
}

/// <summary>A provider-neutral request for one language-model completion.</summary>
public sealed record LlmInferenceRequest
{
    /// <summary>Borrowed caller-owned resource authority. Never part of a portable request.</summary>
    public IInferenceResourceAccess? Resources { get; init; }

    /// <summary>Returns the type name only, so logging a request prints none of its content.</summary>
    public override string ToString() => nameof(LlmInferenceRequest);

    /// <summary>The policy for continuation state the selected implementation cannot replay.</summary>
    public LlmUnboundContinuationState UnboundContinuationState { get; init; }

    /// <summary>The identifier of the model to run.</summary>
    public required string ModelId { get; init; }

    /// <summary>System instructions for the model, when given.</summary>
    public string? Instructions { get; init; }

    /// <summary>The conversation messages, oldest first.</summary>
    public IReadOnlyList<LlmMessage> Messages { get; init; } = [];

    /// <summary>The sampling temperature. Null leaves the provider default.</summary>
    public double? Temperature { get; init; }

    /// <summary>The most output tokens to generate. Null leaves the provider default.</summary>
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
    public LlmReasoningLevel? ReasoningLevel { get; init; }

    /// <summary>The structured output schema the response must match, when requested.</summary>
    public LlmStructuredOutputRequest? StructuredOutput { get; init; }

    /// <summary>Client tools the model may call.</summary>
    public IReadOnlyList<LlmClientToolDefinition> Tools { get; init; } = [];

    /// <summary>Provider-hosted tools the model may use.</summary>
    public IReadOnlyList<LlmHostedTool> HostedTools { get; init; } = [];

    /// <summary>The most turns the provider may run with hosted tools. Null leaves the provider default.</summary>
    public int? MaxHostedTurns { get; init; }

    /// <summary>The tool-choice policy. Null leaves the provider default.</summary>
    public LlmToolChoice? ToolChoice { get; init; }

    /// <summary>Requests or disables parallel tool calls when the provider supports that control.</summary>
    public bool? AllowParallelToolCalls { get; init; }

    /// <summary>A stable conversation affinity key that providers use for prompt-cache routing. It is neither storage nor resume state.</summary>
    public string? ConversationKey { get; init; }

    /// <summary>An advisory request to cache part of the prompt, when given.</summary>
    public LlmPromptCacheHint? PromptCache { get; init; }

    /// <summary>An explicitly selected native cache. It is separate from advisory prompt-cache hints.</summary>
    public LlmExplicitCacheReference? ExplicitCache { get; init; }

    /// <summary>Typed provider controls. Null leaves the selected connection default in place.</summary>
    public LlmProviderControls? ProviderControls { get; init; }
}

/// <summary>Token counts reported for a completion. Null counts are not reported.</summary>
/// <param name="InputTokens">The input tokens, when reported.</param>
/// <param name="OutputTokens">The output tokens, when reported.</param>
/// <param name="TotalTokens">The total tokens, when reported.</param>
public sealed record LlmUsage(int? InputTokens, int? OutputTokens, int? TotalTokens)
{
    private readonly IReadOnlyDictionary<LlmHostedToolKind, int>? _hostedToolCalls;

    /// <summary>Subset of input tokens served from cache; null means not reported.</summary>
    public int? CachedInputTokens { get; init; }

    /// <summary>Subset of input tokens written to cache; null means not reported.</summary>
    public int? CacheWriteInputTokens { get; init; }

    /// <summary>Subset of output tokens used for reasoning; null means not reported.</summary>
    public int? ReasoningOutputTokens { get; init; }

    /// <summary>Actual provider-reported USD cost; null means not reported, never a local estimate.</summary>
    public decimal? ProviderReportedCostUsd { get; init; }

    /// <summary>Reported counts by hosted category. Missing keys are unknown; an empty map reports no named counts.</summary>
    public IReadOnlyDictionary<LlmHostedToolKind, int>? HostedToolCalls
    {
        get => _hostedToolCalls;
        init => _hostedToolCalls = value?.Where(pair => pair.Value >= 0).ToFrozenDictionary();
    }

    /// <summary>Actual total hosted-tool calls, independent of a possibly partial named-count map. Negative observations remain unknown.</summary>
    public int? HostedToolCallCount { get; init => field = value is >= 0 ? value : null; }

    /// <summary>Actual sources used, independent of hosted-tool call counts. Negative observations remain unknown.</summary>
    public int? HostedSourcesUsed { get; init => field = value is >= 0 ? value : null; }
}

/// <summary>How a completion ended.</summary>
public enum LlmCompletionStatus
{
    /// <summary>The model finished its response.</summary>
    Completed,
    /// <summary>The response stopped early, for example at the output token limit.</summary>
    Incomplete,
    /// <summary>The model refused the request.</summary>
    Refused,
    /// <summary>The completion failed.</summary>
    Failed,
    /// <summary>The model stopped to wait for tool results.</summary>
    RequiresAction,

    /// <summary>A terminal response whose natural completion or truncation the provider cannot report.</summary>
    Unknown
}

/// <summary>The outcome of structured output enforcement and local validation.</summary>
/// <param name="Enforcement">How the structured output was enforced.</param>
/// <param name="WasEvaluated">Whether the output was validated against the schema.</param>
/// <param name="IsValid">Whether the output matched the schema.</param>
/// <param name="Errors">The validation errors, empty when the output is valid.</param>
public sealed record LlmStructuredOutputValidation(
    LlmStructuredOutputEnforcement Enforcement,
    bool WasEvaluated,
    bool IsValid,
    IReadOnlyList<string> Errors);

/// <summary>The provider-neutral result of one language-model completion.</summary>
public sealed record LlmInferenceResult
{
    /// <summary>Continuation state to keep unchanged on this response's assistant message.</summary>
    public LlmContinuationState? ContinuationState { get; init; }

    /// <summary>How many request messages were sent without their continuation state.</summary>
    public int OmittedContinuationStateCount { get; init; }

    /// <summary>The generated text.</summary>
    public required string Text { get; init; }

    /// <summary>The refusal text when the model refused.</summary>
    public string? Refusal { get; init; }

    /// <summary>The provider refusal category when the provider names one.</summary>
    public string? RefusalCategory { get; init; }

    /// <summary>How the completion ended.</summary>
    public required LlmCompletionStatus Status { get; init; }

    /// <summary>The provider finish reason, when reported.</summary>
    public string? FinishReason { get; init; }

    /// <summary>The provider reason for an incomplete response, when reported.</summary>
    public string? IncompleteReason { get; init; }

    /// <summary>Token usage, when reported.</summary>
    public LlmUsage? Usage { get; init; }

    /// <summary>Client tool calls the model made.</summary>
    public IReadOnlyList<LlmClientToolCall> ToolCalls { get; init; } = [];

    /// <summary>Citations the model attached to its response.</summary>
    public IReadOnlyList<LlmCitation> Citations { get; init; } = [];

    /// <summary>Ordered actual commit-and-selection receipts for generated session artifacts.</summary>
    public IReadOnlyList<InferenceResourceCommitResult> GeneratedResources
    {
        get;
        init => field = ResourceContract.CopyGeneratedResources(value);
    } = [];

    /// <summary>The structured output validation outcome, when structured output was requested.</summary>
    public LlmStructuredOutputValidation? StructuredOutput { get; init; }

    /// <summary>Which implementation, model and runtime produced the result.</summary>
    public required InferenceProvenance Provenance { get; init; }
}
