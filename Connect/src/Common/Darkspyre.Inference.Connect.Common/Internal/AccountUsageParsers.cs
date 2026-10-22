using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Darkspyre.Inference.Connect.LLM.Internal;

internal static partial class AccountUsageValues
{
    internal static string? Header(HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
    internal static decimal? Decimal(string? value) => decimal.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out decimal number) ? number : null;
    internal static decimal? Nonnegative(string? value) => Decimal(value) is >= 0m and var number ? number : null;
    internal static bool? Boolean(string? value) => value?.ToLowerInvariant() switch
    { "true" or "1" => true, "false" or "0" => false, _ => null };
    internal static string? String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static decimal? Number(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out decimal number) ? number : null;
    internal static DateTimeOffset? Unix(string? value) => long.TryParse(value, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out long seconds) && seconds is >= -62135596800 and <= 253402300799
        ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    internal static DateTimeOffset? Unix(decimal? value) => value is not null && decimal.Truncate(value.Value) == value
        ? Unix(value.Value.ToString(CultureInfo.InvariantCulture)) : null;
    internal static DateTimeOffset? Rfc3339(string? value) => value is not null && Rfc3339Shape().IsMatch(value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
        ? parsed : null;
    internal static LlmQuotaState State(decimal? remaining) => remaining switch
    { <= 0 => LlmQuotaState.Exhausted, > 0 => LlmQuotaState.Available, _ => LlmQuotaState.Unknown };
    internal static LlmQuotaState ClaudeState(string? status) => status switch
    { "rejected" => LlmQuotaState.Exhausted, "allowed" or "allowed_warning" => LlmQuotaState.Available, _ => LlmQuotaState.Unknown };
    internal static LlmAccountUsageSnapshot? Snapshot(DateTimeOffset observedAt, List<LlmQuotaWindow> quotas,
        List<LlmCreditBalance>? credits = null) => quotas.Count == 0 && (credits is null || credits.Count == 0)
        ? null : new() { RetrievedAt = observedAt, Quotas = quotas, Credits = credits ?? [] };

    internal static LlmQuotaWindow Counts(string id, string displayName, string unit, decimal? limit, decimal? remaining,
        DateTimeOffset? reset, LlmAccountUsageObservation observation, bool approximate = false)
    {
        double? fraction = limit > 0 && remaining is not null ? Math.Clamp((double)remaining.Value / (double)limit.Value, 0, 1) : null;
        return new()
        {
            Id = id,
            DisplayName = displayName,
            Unit = unit,
            Limit = limit,
            Remaining = remaining,
            Used = limit >= remaining ? limit - remaining : null,
            RemainingFraction = fraction,
            UsedFraction = fraction is null ? null : 1 - fraction,
            ResetsAt = reset,
            State = State(remaining),
            Observation = observation,
            IsApproximate = approximate
        };
    }

    internal static TimeSpan? Duration(string? value)
    {
        if (string.IsNullOrEmpty(value)) { return null; }
        decimal seconds = 0;
        int consumed = 0;
        foreach (Match match in DurationPart().Matches(value))
        {
            if (match.Index != consumed || Decimal(match.Groups[1].Value) is not { } number) { return null; }
            decimal multiplier = match.Groups[2].Value switch { "h" => 3600, "m" => 60, "s" => 1, _ => 0.001m };
            decimal maximum = (decimal)TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond;
            if (number > maximum / multiplier || seconds > maximum - number * multiplier) { return null; }
            seconds += number * multiplier;
            consumed += match.Length;
        }
        return consumed == value.Length ? TimeSpan.FromTicks((long)(seconds * TimeSpan.TicksPerSecond)) : null;
    }

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)(ms|h|m|s)", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPart();

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt][0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?(?:[Zz]|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339Shape();
}

internal static class OpenAiRateLimitHeaders
{
    internal static LlmAccountUsageSnapshot? Parse(HttpResponseHeaders headers, DateTimeOffset observedAt)
    {
        var quotas = new List<LlmQuotaWindow>();
        foreach ((string id, string name, string unit) in new[]
                 { ("requests", "Requests", "requests"), ("tokens", "Tokens", "tokens"), ("project-tokens", "Project tokens", "tokens") })
        {
            decimal? limit = AccountUsageValues.Nonnegative(AccountUsageValues.Header(headers, "x-ratelimit-limit-" + id));
            decimal? remaining = AccountUsageValues.Nonnegative(AccountUsageValues.Header(headers, "x-ratelimit-remaining-" + id));
            string? rawReset = AccountUsageValues.Header(headers, "x-ratelimit-reset-" + id);
            TimeSpan? duration = AccountUsageValues.Duration(rawReset);
            DateTimeOffset? reset = duration is not null && duration.Value <= DateTimeOffset.MaxValue - observedAt ? observedAt + duration.Value : null;
            if (limit is null && remaining is null && reset is null) { continue; }
            quotas.Add(AccountUsageValues.Counts(id, name, unit, limit, remaining, reset, new()
            {
                ObservedAt = observedAt,
                Source = "openai-rate-limit-headers",
                RawReset = duration is null ? null : rawReset,
                ResetUnit = duration is null ? null : "duration"
            }));
        }
        return AccountUsageValues.Snapshot(observedAt, quotas);
    }
}

internal static class AnthropicRateLimitHeaders
{
    internal static LlmAccountUsageSnapshot? Parse(HttpResponseHeaders headers, DateTimeOffset observedAt)
    {
        var quotas = new List<LlmQuotaWindow>();
        foreach (string id in new[] { "requests", "tokens", "input-tokens", "output-tokens", "priority-input-tokens", "priority-output-tokens" })
        {
            string prefix = id.StartsWith("priority-", StringComparison.Ordinal)
                ? "anthropic-" + id : "anthropic-ratelimit-" + id;
            decimal? limit = AccountUsageValues.Nonnegative(AccountUsageValues.Header(headers, prefix + "-limit"));
            decimal? remaining = AccountUsageValues.Nonnegative(AccountUsageValues.Header(headers, prefix + "-remaining"));
            string? rawReset = AccountUsageValues.Header(headers, prefix + "-reset");
            DateTimeOffset? reset = AccountUsageValues.Rfc3339(rawReset);
            if (limit is null && remaining is null && reset is null) { continue; }
            quotas.Add(AccountUsageValues.Counts(id, id.Replace('-', ' '), id == "requests" ? "requests" : "tokens", limit, remaining, reset, new()
            {
                ObservedAt = observedAt,
                Source = "anthropic-rate-limit-headers",
                RawReset = reset is null ? null : rawReset,
                ResetUnit = reset is null ? null : "rfc3339"
            }, approximate: id != "requests"));
        }
        return AccountUsageValues.Snapshot(observedAt, quotas);
    }
}

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
        if (AccountUsageValues.ClaudeState(overageStatus) != LlmQuotaState.Unknown || AccountUsageValues.Unix(overageReset) is not null)
        {
            LlmQuotaWindow? sameBucket = quotas.FirstOrDefault(quota => quota.Id == "overage");
            quotas.RemoveAll(quota => quota.Id == "overage");
            quotas.Add(new()
            {
                Id = "overage",
                DisplayName = "Overage",
                State = AccountUsageValues.ClaudeState(overageStatus),
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

    private static LlmQuotaWindow? Window(string id, string? status, decimal? rawUtilization, decimal? rawReset, DateTimeOffset observedAt)
    {
        double? fraction = rawUtilization is >= 0 and <= 1 ? (double)rawUtilization.Value : null;
        DateTimeOffset? reset = AccountUsageValues.Unix(rawReset);
        LlmQuotaState state = status is null
            ? fraction is null ? LlmQuotaState.Unknown : fraction >= 1 ? LlmQuotaState.Exhausted : LlmQuotaState.Available
            : AccountUsageValues.ClaudeState(status);
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
