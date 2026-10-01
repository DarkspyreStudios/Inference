using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Projects the authenticated key's limit, not an inferred account balance. Labels stay private.</summary>
internal static class OpenRouterAccountUsageParser
{
    internal static LlmAccountUsageSnapshot Parse(JsonElement root, DateTimeOffset retrievedAt, LlmCredential? credential = null)
    {
        JsonElement data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out JsonElement value)
            && value.ValueKind == JsonValueKind.Object ? value : default;
        var quotas = new List<LlmQuotaWindow>();
        var credits = new List<LlmCreditBalance>();
        var metrics = new List<LlmUsageMetric>();
        decimal? limit = AccountUsageValues.Number(data, "limit");
        decimal? used = AccountUsageValues.Number(data, "usage");
        decimal? remaining = AccountUsageValues.Number(data, "limit_remaining");
        if (limit is >= 0)
        {
            // Provider-authored reset display text crosses the existing secret/email boundary once.
            string? reset = AccountUsageValues.String(data, "limit_reset");
            string display = reset is null ? "Key limit" : "Key limit (" + AccountUsageSanitizer.Sanitize(reset, credential) + " reset)";
            quotas.Add(new()
            {
                Id = "key-limit", DisplayName = display, Unit = "credits", Limit = limit, Used = used, Remaining = remaining,
                UsedFraction = used is >= 0 && limit > 0 ? Math.Clamp((double)used.Value / (double)limit.Value, 0, 1) : null,
                RemainingFraction = remaining is not null && limit > 0 ? Math.Clamp((double)remaining.Value / (double)limit.Value, 0, 1) : null,
                State = AccountUsageValues.State(remaining)
            });
        }
        else if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("limit", out value) && value.ValueKind == JsonValueKind.Null)
        { credits.Add(new() { Id = "key-limit", DisplayName = "Key limit", IsUnlimited = true }); }
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("free_model_daily_requests", out JsonElement daily)
            && daily.ValueKind == JsonValueKind.Object)
        {
            quotas.Add(new()
            {
                Id = "free-model-daily-requests", DisplayName = "Free-model daily requests", Unit = "requests",
                Limit = AccountUsageValues.Number(daily, "limit"), Used = AccountUsageValues.Number(daily, "used"),
                Remaining = AccountUsageValues.Number(daily, "remaining"), WindowDuration = TimeSpan.FromDays(1),
                State = AccountUsageValues.State(AccountUsageValues.Number(daily, "remaining"))
            });
        }
        foreach ((string field, string id, TimeSpan? duration) in new (string, string, TimeSpan?)[]
                 { ("usage_daily", "usage:daily", TimeSpan.FromDays(1)), ("usage_weekly", "usage:weekly", TimeSpan.FromDays(7)),
                   ("usage_monthly", "usage:monthly", null), ("usage", "usage:all-time", null) })
        {
            if (AccountUsageValues.Number(data, field) is { } number)
            { metrics.Add(new() { Id = id, Value = number, Unit = "credits", PeriodDuration = duration }); }
        }
        return new()
        {
            RetrievedAt = retrievedAt,
            Plan = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("is_free_tier", out value)
                && value.ValueKind == JsonValueKind.True ? "free" : null,
            Quotas = quotas, Credits = credits, Metrics = metrics
        };
    }
}
