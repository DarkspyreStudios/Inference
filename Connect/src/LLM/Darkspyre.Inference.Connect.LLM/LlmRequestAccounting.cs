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
    public bool IsSupported { get; init; }
    /// <summary>Safe explanatory text, never raw provider diagnostics.</summary>
    public string? Diagnostic { get; init; }
    /// <summary>Live revision against which a session proposal was measured, not its proposed revision.</summary>
    public long? ContextRevision { get; init; }
    public long? ModelMaxContextTokens { get; init; }
    public long? ModelMaxOutputTokens { get; init; }
    /// <summary>Effective context ceiling after the host policy, not proof of a provider limit.</summary>
    public long? MaxContextTokens { get; init; }
    public long? UsedContextTokens { get; init; }
    public long? ReservedOutputTokens { get; init; }
    public long? SafetyMarginTokens { get; init; }
    public long? InputBudgetTokens { get; init; }
    public long? AvailableInputTokens { get; init; }
    public bool? NeedsCompaction { get; init; }
    public LlmContextMeasurementSource Source { get; init; }
    public LlmOutputLimitEnforcement OutputLimitEnforcement { get; init; }
}
