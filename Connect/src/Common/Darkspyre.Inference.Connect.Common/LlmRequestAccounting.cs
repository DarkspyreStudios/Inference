using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Nonmutating accounting of one projected request. Supported does not mean measurable or guaranteed
/// to fit. Unknown components remain null; estimates are not exact admission proofs.
/// </summary>
public sealed record LlmRequestAccounting
{
    /// <summary>Null when an unsupported native route does not expose its resolved model.</summary>
    public required string? ModelId { get; init; }
    /// <summary>Whether the route can account for the request at all.</summary>
    public bool IsSupported { get; init; }
    /// <summary>Safe explanatory text, never raw provider diagnostics.</summary>
    public string? Diagnostic { get; init; }
    /// <summary>Live revision against which a session proposal was measured, not its proposed revision.</summary>
    public long? ContextRevision { get; init; }
    /// <summary>The model's context window in tokens, when known.</summary>
    public long? ModelMaxContextTokens { get; init; }
    /// <summary>The model's output token limit, when known.</summary>
    public long? ModelMaxOutputTokens { get; init; }
    /// <summary>Effective context ceiling after the host policy, not proof of a provider limit.</summary>
    public long? MaxContextTokens { get; init; }
    /// <summary>The tokens the request's input occupies, when measured or estimated.</summary>
    public long? UsedContextTokens { get; init; }
    /// <summary>The tokens held back from the context window for the response.</summary>
    public long? ReservedOutputTokens { get; init; }
    /// <summary>The tokens of the context window usable for input after the response reserve.</summary>
    public long? InputBudgetTokens { get; init; }
    /// <summary>The input tokens still free within <see cref="InputBudgetTokens"/>, when both sides are known.</summary>
    public long? AvailableInputTokens { get; init; }
    /// <summary>Whether the input has reached the compaction trigger; null when unknown.</summary>
    public bool? NeedsCompaction { get; init; }
    /// <summary>How <see cref="UsedContextTokens"/> was obtained.</summary>
    public LlmContextMeasurementSource Source { get; init; }
    /// <summary>Who enforces a requested output-token cap on this route.</summary>
    public LlmOutputLimitEnforcement OutputLimitEnforcement { get; init; }
}
