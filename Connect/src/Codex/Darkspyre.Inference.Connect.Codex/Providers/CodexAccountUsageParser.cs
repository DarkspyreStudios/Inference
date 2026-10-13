using System.Globalization;
using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Read-only WHAM quota projection. Missing measurements remain unknown, never zero.</summary>
internal static class CodexAccountUsageParser
{
    public static LlmAccountUsageSnapshot Parse(JsonElement root, DateTimeOffset retrievedAt)
    {
        var quotas = new List<LlmQuotaWindow>();
        var credits = new List<LlmCreditBalance>();
        AddLimits(Object(root, "rate_limit"), "codex", quotas);
        if (root.TryGetProperty("additional_rate_limits", out var additional) && additional.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in additional.EnumerateArray())
            {
                string? meter = String(item, "metered_feature") ?? String(item, "limit_name");
                if (meter is not null) { AddLimits(Object(item, "rate_limit"), meter, quotas); }
            }
        }
        var balance = Object(root, "credits");
        if (balance.ValueKind == JsonValueKind.Object)
        {
            credits.Add(new()
            {
                Id = "workspace-credits",
                DisplayName = "Workspace credits",
                Remaining = Decimal(balance, "balance"),
                HasCredits = Boolean(balance, "has_credits"),
                IsUnlimited = Boolean(balance, "unlimited")
            });
        }
        if (Number(Object(root, "rate_limit_reset_credits"), "available_count") is long count && count >= 0)
        {
            credits.Add(new()
            {
                Id = "rate-limit-resets",
                DisplayName = "Rate-limit resets",
                Remaining = count,
                Unit = "resets",
                HasCredits = count > 0
            });
        }
        return new()
        {
            RetrievedAt = retrievedAt,
            Plan = String(root, "plan_type"),
            Quotas = quotas,
            Credits = credits,
            Summary = "Direct subscription quota snapshot. Activity aggregates and reset-credit expiry are unavailable."
        };
    }

    private static void AddLimits(JsonElement limits, string meter, List<LlmQuotaWindow> quotas)
    {
        foreach (string name in new[] { "primary_window", "secondary_window" })
        {
            var window = Object(limits, name);
            if (window.ValueKind != JsonValueKind.Object) { continue; }
            decimal? percent = Decimal(window, "used_percent");
            double? used = percent is null ? null : Math.Clamp((double)percent / 100d, 0d, 1d);
            long? seconds = Number(window, "limit_window_seconds");
            long? reset = Number(window, "reset_at");
            quotas.Add(new()
            {
                Id = meter + ":" + name.Replace("_window", "", StringComparison.Ordinal),
                DisplayName = meter + " (" + name.Replace("_window", "", StringComparison.Ordinal) + ")",
                MeterId = meter,
                UsedFraction = used,
                RemainingFraction = used is null ? null : 1d - used,
                WindowDuration = seconds is >= 0 and <= 315576000 ? TimeSpan.FromSeconds(seconds.Value) : null,
                ResetsAt = reset is >= -62135596800 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(reset.Value) : null,
                State = Boolean(limits, "allowed") == false || Boolean(limits, "limit_reached") == true
                    ? LlmQuotaState.Exhausted : used is null ? LlmQuotaState.Unknown
                    : used >= 1d ? LlmQuotaState.Exhausted : LlmQuotaState.Available
            });
        }
    }

    private static JsonElement Object(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Object ? value : default;
    private static string? String(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool? Boolean(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static long? Number(JsonElement root, string key) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;
    private static decimal? Decimal(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var value)) { return null; }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number)) { return number; }
        return value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number) ? number : null;
    }
}
