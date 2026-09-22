namespace Darkspyre.Inference.Connect.LLM;

/// <summary>The current state of a metered provider quota.</summary>
public enum LlmQuotaState
{
    Unknown,
    Available,
    Exhausted
}

/// <summary>A rolling, calendar, model, or provider-defined quota window.</summary>
public sealed record LlmQuotaWindow
{
    /// <summary>Stable identifier within this provider's account-usage result.</summary>
    public required string Id { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>Provider meter or model identifier when the quota is scoped to one.</summary>
    public string? MeterId { get; init; }

    /// <summary>Value from zero through one when the provider reports proportional usage.</summary>
    public double? UsedFraction { get; init; }

    /// <summary>Value from zero through one when the provider reports proportional remaining quota.</summary>
    public double? RemainingFraction { get; init; }

    public decimal? Limit { get; init; }

    public decimal? Used { get; init; }

    public decimal? Remaining { get; init; }

    /// <summary>Unit for <see cref="Limit"/>, <see cref="Used"/>, and <see cref="Remaining"/>.</summary>
    public string? Unit { get; init; }

    public TimeSpan? WindowDuration { get; init; }

    public DateTimeOffset? ResetsAt { get; init; }

    public LlmQuotaState State { get; init; }

    /// <summary>True when the provider identifies the value as estimated or locally derived.</summary>
    public bool IsApproximate { get; init; }
}

/// <summary>A provider credit balance associated with an authenticated account.</summary>
public sealed record LlmCreditBalance
{
    public required string Id { get; init; }

    public string? DisplayName { get; init; }

    public decimal? Remaining { get; init; }

    public string Unit { get; init; } = "credits";

    public bool? HasCredits { get; init; }

    public bool IsUnlimited { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public bool IsApproximate { get; init; }
}

/// <summary>An aggregate account activity measurement.</summary>
public sealed record LlmUsageMetric
{
    public required string Id { get; init; }

    public string? DisplayName { get; init; }

    public required decimal Value { get; init; }

    /// <summary>Canonical lowercase unit such as tokens, requests, sessions, seconds, or days.</summary>
    public required string Unit { get; init; }

    public DateOnly? PeriodStart { get; init; }

    public TimeSpan? PeriodDuration { get; init; }

    public bool IsApproximate { get; init; }
}

/// <summary>Account-level subscription quota and activity reported by a provider.</summary>
public sealed record LlmAccountUsageSnapshot
{
    public required DateTimeOffset RetrievedAt { get; init; }

    public string? Plan { get; init; }

    /// <summary>
    /// Sanitized provider-authored display text when the CLI exposes more detail than its structured
    /// protocol. Callers may display this value but must not parse it as a stable contract.
    /// </summary>
    public string? Summary { get; init; }

    public IReadOnlyList<LlmQuotaWindow> Quotas { get; init; } = [];

    public IReadOnlyList<LlmCreditBalance> Credits { get; init; } = [];

    public IReadOnlyList<LlmUsageMetric> Metrics { get; init; } = [];
}

/// <summary>An optional adapter capability for reading account-level subscription usage.</summary>
public interface ILlmAccountUsageClient
{
    LlmTransport Transport { get; }

    Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default);
}
