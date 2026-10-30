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

// Anthropic Messages uses this; Anthropic Messages is out of the build (Providers/AnthropicMessagesLlmClient.cs, under
// Compile Remove). It moves into that provider's own library when the provider returns to the build.
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
