namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The current state of a metered provider quota.</summary>
public enum LlmQuotaState
{
    /// <summary>The provider does not report whether quota remains.</summary>
    Unknown,
    /// <summary>Quota remains.</summary>
    Available,
    /// <summary>The quota is used up.</summary>
    Exhausted
}

/// <summary>Evidence attached to one passively observed account measurement.</summary>
public sealed record LlmAccountUsageObservation
{
    /// <summary>When the measurement was observed.</summary>
    public required DateTimeOffset ObservedAt { get; init; }
    /// <summary>Where the measurement came from, such as <c>openai-rate-limit-headers</c>.</summary>
    public required string Source { get; init; }
    /// <summary>When the measurement stops being current, or null when unknown.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
    /// <summary>The utilization value as the provider reported it, or null.</summary>
    public decimal? RawUtilization { get; init; }
    /// <summary>The unit of <see cref="RawUtilization"/>, such as <c>percent</c> or <c>fraction</c>, or null.</summary>
    public string? UtilizationUnit { get; init; }
    /// <summary>The reset value as the provider reported it, or null.</summary>
    public string? RawReset { get; init; }
    /// <summary>The format of <see cref="RawReset"/>, such as <c>duration</c> or <c>rfc3339</c>, or null.</summary>
    public string? ResetUnit { get; init; }
}

/// <summary>A rolling, calendar, model, or provider-defined quota window.</summary>
public sealed record LlmQuotaWindow
{
    /// <summary>The evidence behind the quota values, or null.</summary>
    public LlmAccountUsageObservation? Observation { get; init; }
    /// <summary>Stable identifier within this provider's account-usage result.</summary>
    public required string Id { get; init; }

    /// <summary>The quota name shown to the user, or null.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Provider meter or model identifier when the quota is scoped to one.</summary>
    public string? MeterId { get; init; }

    /// <summary>Value from zero through one when the provider reports proportional usage.</summary>
    public double? UsedFraction { get; init; }

    /// <summary>Value from zero through one when the provider reports proportional remaining quota.</summary>
    public double? RemainingFraction { get; init; }

    /// <summary>The quota size in <see cref="Unit"/>, or null when unknown.</summary>
    public decimal? Limit { get; init; }

    /// <summary>The amount used in <see cref="Unit"/>, or null when unknown.</summary>
    public decimal? Used { get; init; }

    /// <summary>The amount remaining in <see cref="Unit"/>, or null when unknown.</summary>
    public decimal? Remaining { get; init; }

    /// <summary>Unit for <see cref="Limit"/>, <see cref="Used"/>, and <see cref="Remaining"/>.</summary>
    public string? Unit { get; init; }

    /// <summary>The length of the quota window, or null when unknown.</summary>
    public TimeSpan? WindowDuration { get; init; }

    /// <summary>When the quota resets, or null when unknown.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Whether quota remains.</summary>
    public LlmQuotaState State { get; init; }

    /// <summary>True when the provider identifies the value as estimated or locally derived.</summary>
    public bool IsApproximate { get; init; }
}

/// <summary>A provider credit balance associated with an authenticated account.</summary>
public sealed record LlmCreditBalance
{
    /// <summary>The evidence behind the balance, or null.</summary>
    public LlmAccountUsageObservation? Observation { get; init; }
    /// <summary>Stable identifier within this provider's account-usage result.</summary>
    public required string Id { get; init; }

    /// <summary>The balance name shown to the user, or null.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The remaining balance in <see cref="Unit"/>, or null when unknown.</summary>
    public decimal? Remaining { get; init; }

    /// <summary>The unit of the balance.</summary>
    public string Unit { get; init; } = "credits";

    /// <summary>Whether any credit remains, or null when unknown.</summary>
    public bool? HasCredits { get; init; }

    /// <summary>Null when the provider does not report whether the balance is unlimited.</summary>
    public bool? IsUnlimited { get; init; }

    /// <summary>When the credits expire, or null when unknown.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>True when the provider identifies the value as estimated or locally derived.</summary>
    public bool IsApproximate { get; init; }
}

/// <summary>An aggregate account activity measurement.</summary>
public sealed record LlmUsageMetric
{
    /// <summary>Stable identifier within this provider's account-usage result.</summary>
    public required string Id { get; init; }

    /// <summary>The metric name shown to the user, or null.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The measured amount in <see cref="Unit"/>.</summary>
    public required decimal Value { get; init; }

    /// <summary>Canonical lowercase unit such as tokens, requests, sessions, seconds, or days.</summary>
    public required string Unit { get; init; }

    /// <summary>The first day of the measured period, or null when unknown.</summary>
    public DateOnly? PeriodStart { get; init; }

    /// <summary>The length of the measured period, or null when unknown.</summary>
    public TimeSpan? PeriodDuration { get; init; }

    /// <summary>True when the provider identifies the value as estimated or locally derived.</summary>
    public bool IsApproximate { get; init; }
}

/// <summary>Account-level subscription quota and activity reported by a provider.</summary>
public sealed record LlmAccountUsageSnapshot
{
    /// <summary>When the snapshot was retrieved.</summary>
    public required DateTimeOffset RetrievedAt { get; init; }

    /// <summary>The subscription plan name, or null when unknown.</summary>
    public string? Plan { get; init; }

    /// <summary>
    /// Sanitized provider-authored display text when the CLI exposes more detail than its structured
    /// protocol. Callers may display this value but must not parse it as a stable contract.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>The quota windows reported.</summary>
    public IReadOnlyList<LlmQuotaWindow> Quotas { get; init; } = [];

    /// <summary>The credit balances reported.</summary>
    public IReadOnlyList<LlmCreditBalance> Credits { get; init; } = [];

    /// <summary>The activity metrics reported.</summary>
    public IReadOnlyList<LlmUsageMetric> Metrics { get; init; } = [];
}

/// <summary>An optional adapter capability for reading account-level subscription usage.</summary>
public interface ILlmAccountUsageClient
{
    /// <summary>The transport this client reads usage for.</summary>
    LlmTransport Transport { get; }

    /// <summary>Reads the account usage for the connection.</summary>
    Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default);
}
