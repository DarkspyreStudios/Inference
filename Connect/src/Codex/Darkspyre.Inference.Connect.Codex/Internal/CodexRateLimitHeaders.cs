using System.Net.Http.Headers;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Reads the Codex quota and workspace-credit headers of a ChatGPT subscription response.</summary>
internal static class CodexRateLimitHeaders
{
    internal static LlmAccountUsageSnapshot? Parse(HttpResponseHeaders headers, DateTimeOffset observedAt)
    {
        var quotas = new List<LlmQuotaWindow>();
        var meters = new HashSet<string>(StringComparer.Ordinal) { "codex" };
        foreach (var header in headers)
        {
            string key = header.Key.ToLowerInvariant();
            const string suffix = "-primary-used-percent";
            if (key.StartsWith("x-", StringComparison.Ordinal) && key.EndsWith(suffix, StringComparison.Ordinal))
            { meters.Add(key[2..^suffix.Length]); }
        }
        string? reached = AccountUsageValues.Header(headers, "x-codex-rate-limit-reached-type");
        foreach (string meter in meters.Order(StringComparer.Ordinal))
        {
            foreach (string window in new[] { "primary", "secondary" })
            {
                string prefix = "x-" + meter + "-" + window;
                decimal? percent = AccountUsageValues.Decimal(AccountUsageValues.Header(headers, prefix + "-used-percent"));
                decimal? minutes = AccountUsageValues.Nonnegative(AccountUsageValues.Header(headers, prefix + "-window-minutes"));
                TimeSpan? duration = minutes is not null && minutes.Value <= (decimal)TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMinute
                    ? TimeSpan.FromTicks((long)(minutes.Value * TimeSpan.TicksPerMinute)) : null;
                string? rawReset = AccountUsageValues.Header(headers, prefix + "-reset-at");
                DateTimeOffset? reset = AccountUsageValues.Unix(rawReset);
                if (percent is null && duration is null && reset is null) { continue; }
                double? fraction = percent is null ? null : Math.Clamp((double)percent.Value / 100, 0, 1);
                string id = meter.Replace('-', '_');
                quotas.Add(new()
                {
                    Id = id + ":" + window,
                    MeterId = id,
                    DisplayName = id + " (" + window + ")",
                    UsedFraction = fraction,
                    RemainingFraction = fraction is null ? null : 1 - fraction,
                    WindowDuration = duration,
                    ResetsAt = reset,
                    State = fraction is null ? LlmQuotaState.Unknown
                        : fraction >= 1 ? LlmQuotaState.Exhausted : LlmQuotaState.Available,
                    Observation = new()
                    {
                        ObservedAt = observedAt,
                        Source = "codex-quota-headers",
                        RawUtilization = percent,
                        UtilizationUnit = percent is null ? null : "percent",
                        RawReset = reset is null ? null : rawReset,
                        ResetUnit = reset is null ? null : "unix-seconds"
                    }
                });
            }
        }
        if (reached is "rate_limit_reached" or "workspace_owner_usage_limit_reached" or "workspace_member_usage_limit_reached")
        {
            bool measured = quotas.Any(quota => quota.MeterId == "codex" && quota.UsedFraction is not null);
            for (int index = 0; index < quotas.Count; index++)
            {
                if (quotas[index].MeterId == "codex" && (!measured || quotas[index].UsedFraction >= 1))
                { quotas[index] = quotas[index] with { State = LlmQuotaState.Exhausted }; }
            }
        }
        var credits = new List<LlmCreditBalance>();
        bool? hasCredits = AccountUsageValues.Boolean(AccountUsageValues.Header(headers, "x-codex-credits-has-credits"));
        bool? unlimited = AccountUsageValues.Boolean(AccountUsageValues.Header(headers, "x-codex-credits-unlimited"));
        decimal? balance = AccountUsageValues.Decimal(AccountUsageValues.Header(headers, "x-codex-credits-balance"));
        if (reached is "workspace_owner_credits_depleted" or "workspace_member_credits_depleted") { hasCredits = false; }
        if (hasCredits is not null || unlimited is not null || balance is not null)
        {
            credits.Add(new()
            {
                Id = "workspace-credits",
                DisplayName = "Workspace credits",
                Remaining = balance,
                HasCredits = hasCredits,
                IsUnlimited = unlimited,
                Observation = new() { ObservedAt = observedAt, Source = "codex-quota-headers" }
            });
        }
        return AccountUsageValues.Snapshot(observedAt, quotas, credits);
    }
}
