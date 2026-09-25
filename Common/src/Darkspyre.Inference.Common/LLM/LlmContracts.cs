using System.Text.Json;

namespace Darkspyre.Inference.Common.LLM;

public sealed record LlmMessage(string Role, string Content)
{
    public IReadOnlyList<LlmAttachment> Attachments { get; init; } = [];

    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];

    public IReadOnlyList<LlmToolResult> ToolResults { get; init; } = [];
}

/// <summary>
/// Provider-neutral binary content supplied with a prompt. Adapters own any base64, data-URL, upload or
/// provider content-block representation. A set <see cref="MediaType"/> is used as given; otherwise the
/// adapter detects the type from the bytes.
/// </summary>
public sealed record LlmAttachment
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

public sealed record LlmInferenceRequest
{
    public required string ModelId { get; init; }

    public string? Instructions { get; init; }

    public IReadOnlyList<LlmMessage> Messages { get; init; } = [];

    public double? Temperature { get; init; }

    public int? MaxOutputTokens { get; init; }

    /// <summary>The requested reasoning level; null leaves the provider's default in place.</summary>
    public LlmReasoningLevel? ReasoningLevel { get; init; }

    public LlmStructuredOutputRequest? StructuredOutput { get; init; }

    public IReadOnlyList<LlmToolDefinition> Tools { get; init; } = [];

    public LlmToolChoice? ToolChoice { get; init; }

    public bool? AllowParallelToolCalls { get; init; }

    public string? ConversationKey { get; init; }
}

public sealed record LlmUsage(int? InputTokens, int? OutputTokens, int? TotalTokens);

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

public sealed record LlmInferenceResult
{
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
