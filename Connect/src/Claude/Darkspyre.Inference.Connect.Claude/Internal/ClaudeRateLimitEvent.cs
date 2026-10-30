using System.Globalization;
using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Reads the Claude CLI's rate_limit_event into account usage.</summary>
internal static class ClaudeRateLimitEvent
{
    internal static LlmAccountUsageSnapshot? Parse(JsonElement root, DateTimeOffset observedAt)
    {
        if (root.ValueKind != JsonValueKind.Object) { return null; }
        string? status = AccountUsageValues.String(root, "status");
        string? type = AccountUsageValues.String(root, "rateLimitType");
        string id = type ?? "current";
        var quotas = new List<LlmQuotaWindow>();
        if (root.TryGetProperty("unifiedWindows", out JsonElement windows) && windows.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty window in windows.EnumerateObject())
            {
                if (Window(window.Name, window.Name == id ? status : null, AccountUsageValues.Number(window.Value, "utilization"),
                        AccountUsageValues.Number(window.Value, "resetsAt"), observedAt) is { } quota)
                { quotas.Add(quota); }
            }
        }
        if (quotas.All(quota => quota.Id != id)
            && Window(id, status, null, AccountUsageValues.Number(root, "resetsAt"), observedAt) is { } reported)
        { quotas.Add(reported); }
        string? overageStatus = AccountUsageValues.String(root, "overageStatus");
        decimal? overageReset = AccountUsageValues.Number(root, "overageResetsAt");
        if (ClaudeState(overageStatus) != LlmQuotaState.Unknown || AccountUsageValues.Unix(overageReset) is not null)
        {
            LlmQuotaWindow? sameBucket = quotas.FirstOrDefault(quota => quota.Id == "overage");
            quotas.RemoveAll(quota => quota.Id == "overage");
            quotas.Add(new()
            {
                Id = "overage",
                DisplayName = "Overage",
                State = ClaudeState(overageStatus),
                UsedFraction = sameBucket?.UsedFraction,
                RemainingFraction = sameBucket?.RemainingFraction,
                ResetsAt = AccountUsageValues.Unix(overageReset) ?? sameBucket?.ResetsAt,
                Observation = new()
                {
                    ObservedAt = observedAt,
                    Source = "claude-cli-rate-limit-event",
                    RawUtilization = sameBucket?.Observation?.RawUtilization,
                    UtilizationUnit = sameBucket?.Observation?.UtilizationUnit,
                    RawReset = overageReset?.ToString(CultureInfo.InvariantCulture) ?? sameBucket?.Observation?.RawReset,
                    ResetUnit = overageReset is null ? sameBucket?.Observation?.ResetUnit : "unix-seconds"
                }
            });
        }
        return AccountUsageValues.Snapshot(observedAt, quotas);
    }

    /// <summary>The quota state a Claude rate-limit status reports.</summary>
    internal static LlmQuotaState ClaudeState(string? status) => status switch
    { "rejected" => LlmQuotaState.Exhausted, "allowed" or "allowed_warning" => LlmQuotaState.Available, _ => LlmQuotaState.Unknown };

    private static LlmQuotaWindow? Window(string id, string? status, decimal? rawUtilization, decimal? rawReset, DateTimeOffset observedAt)
    {
        double? fraction = rawUtilization is >= 0 and <= 1 ? (double)rawUtilization.Value : null;
        DateTimeOffset? reset = AccountUsageValues.Unix(rawReset);
        LlmQuotaState state = status is null
            ? fraction is null ? LlmQuotaState.Unknown : fraction >= 1 ? LlmQuotaState.Exhausted : LlmQuotaState.Available
            : ClaudeState(status);
        if (fraction is null && reset is null && state == LlmQuotaState.Unknown) { return null; }
        return new()
        {
            Id = id,
            DisplayName = id switch
            {
                "five_hour" => "5-hour limit",
                "seven_day" => "7-day limit",
                "seven_day_opus" => "7-day Opus limit",
                "seven_day_sonnet" => "7-day Sonnet limit",
                "seven_day_overage_included" => "7-day limit (overage included)",
                "overage" => "Overage",
                "current" => "Current limit",
                _ => id.Replace('_', ' ')
            },
            UsedFraction = fraction,
            RemainingFraction = fraction is null ? null : 1 - fraction,
            ResetsAt = reset,
            State = state,
            Observation = new()
            {
                ObservedAt = observedAt,
                Source = "claude-cli-rate-limit-event",
                RawUtilization = rawUtilization,
                UtilizationUnit = rawUtilization is null ? null : "fraction",
                RawReset = rawReset?.ToString(CultureInfo.InvariantCulture),
                ResetUnit = rawReset is null ? null : "unix-seconds"
            }
        };
    }
}
