using System.Collections.Frozen;
using System.Text.Json;
using Darkspyre.Inference.Common.Resources;

namespace Darkspyre.Inference.Common.LLM;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmMessage(string Role, string Content)
{
    public LlmContinuationState? ContinuationState { get; init; }

    public IReadOnlyList<LlmMessageAttachment> Attachments { get; init; } = [];

    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];

    public IReadOnlyList<LlmToolResult> ToolResults { get; init; } = [];
}

/// <summary>
/// Provider-neutral binary content supplied with a prompt. Adapters own any base64, data-URL, upload or
/// provider content-block representation. A set <see cref="MediaType"/> is used as given; otherwise the
/// adapter detects the type from the bytes.
/// </summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmAttachment : LlmMessageAttachment
{
    public required LlmBinaryKind Kind { get; init; }

    public required string FileName { get; init; }

    public string? MediaType { get; init; }

    public required ReadOnlyMemory<byte> Data { get; init; }
}

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

public sealed record LlmToolChoice
{
    public LlmToolChoiceMode Mode { get; init; } = LlmToolChoiceMode.Auto;

    public string? Name { get; init; }
}

public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>The output of a tool invocation executed by the host application.</summary>
public sealed record LlmToolResult(string ToolCallId, LlmToolOutput Output);

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

    public LlmStructuredOutputFallback Fallback { get; init; }
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
    public LlmPromptCacheScope Scope { get; init; } = LlmPromptCacheScope.Conversation;

    public LlmPromptCacheRetention Retention { get; init; }
}

/// <summary>How a route caches prompts. Hints do not guarantee a cache hit.</summary>
public sealed record LlmPromptCacheCapabilities
{
    /// <summary>The provider caches repeated prefixes without a hint.</summary>
    public bool Automatic { get; init; }

    /// <summary>The route converts cache hints into provider cache markers.</summary>
    public bool AcceptsHints { get; init; }

    /// <summary>Retentions the route honours for a hint. Empty when it accepts no hints.</summary>
    public IReadOnlySet<LlmPromptCacheRetention> Retentions { get; init; } = FrozenSet<LlmPromptCacheRetention>.Empty;
}

public sealed record LlmInferenceRequest
{
    /// <summary>Borrowed caller-owned resource authority. Never part of a portable request.</summary>
    public IInferenceResourceAccess? Resources { get; init; }

    public override string ToString() => nameof(LlmInferenceRequest);

    public LlmUnboundContinuationState UnboundContinuationState { get; init; }

    public required string ModelId { get; init; }

    public string? Instructions { get; init; }

    public IReadOnlyList<LlmMessage> Messages { get; init; } = [];

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
    public LlmReasoningLevel? ReasoningLevel { get; init; }

    public LlmStructuredOutputRequest? StructuredOutput { get; init; }

    public IReadOnlyList<LlmToolDefinition> Tools { get; init; } = [];

    public LlmToolChoice? ToolChoice { get; init; }

    public bool? AllowParallelToolCalls { get; init; }

    public string? ConversationKey { get; init; }

    public LlmPromptCacheHint? PromptCache { get; init; }
}

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

public enum LlmCompletionStatus
{
    Completed,
    Incomplete,
    Refused,
    Failed,
    RequiresAction,

    /// <summary>A terminal response whose natural completion or truncation the provider cannot report.</summary>
    Unknown
}

public sealed record LlmStructuredOutputValidation(
    LlmStructuredOutputEnforcement Enforcement,
    bool WasEvaluated,
    bool IsValid,
    IReadOnlyList<string> Errors);

public sealed record LlmInferenceResult
{
    public LlmContinuationState? ContinuationState { get; init; }

    public int OmittedContinuationStateCount { get; init; }

    public required string Text { get; init; }

    public string? Refusal { get; init; }

    public string? RefusalCategory { get; init; }

    public required LlmCompletionStatus Status { get; init; }

    public string? FinishReason { get; init; }

    public string? IncompleteReason { get; init; }

    public LlmUsage? Usage { get; init; }

    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];

    public LlmStructuredOutputValidation? StructuredOutput { get; init; }

    public required InferenceProvenance Provenance { get; init; }
}
