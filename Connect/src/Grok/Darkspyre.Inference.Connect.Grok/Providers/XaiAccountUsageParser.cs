using System.Globalization;
using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Projects the subscription proxy's credits configuration (GET billing?format=credits) onto an account-usage snapshot:
/// one quota window for the current weekly or monthly period, the prepaid balance and, when on-demand use is enabled, the
/// on-demand amount left. The reply is proto3 JSON, which omits zero values, so an absent percentage is 0% used and an
/// absent or empty cent amount is $0. A reply of any other shape throws a <see cref="FormatException"/> that names the
/// field and carries no value.
/// </summary>
internal static class XaiAccountUsageParser
{
    public static LlmAccountUsageSnapshot Parse(JsonElement root, DateTimeOffset retrievedAt)
    {
        JsonElement config = Object(root, "config", "config")
            ?? throw Unrecognized("config", "is missing");
        double percent = Number(config, "creditUsagePercent", "config.creditUsagePercent") ?? 0d;
        if (percent is < 0d or > 100d) throw Unrecognized("config.creditUsagePercent", "is out of range");
        double used = percent / 100d;
        JsonElement period = Object(config, "currentPeriod", "config.currentPeriod")
            ?? throw Unrecognized("config.currentPeriod", "is missing");
        (string id, string name) = String(period, "type", "config.currentPeriod.type") switch
        {
            "USAGE_PERIOD_TYPE_WEEKLY" => ("weekly", "Weekly limit"),
            "USAGE_PERIOD_TYPE_MONTHLY" => ("monthly", "Monthly limit"),
            // proto3 omits the enum's zero value, UNSPECIFIED; Grok Build labels that period "Usage".
            null or "USAGE_PERIOD_TYPE_UNSPECIFIED" => ("period", "Usage limit"),
            _ => throw Unrecognized("config.currentPeriod.type", "is not a known period type")
        };
        DateTimeOffset start = Time(period, "start", "config.currentPeriod.start");
        DateTimeOffset end = Time(period, "end", "config.currentPeriod.end");
        if (end <= start) throw Unrecognized("config.currentPeriod", "ends before it starts");

        decimal prepaid = Cents(config, "prepaidBalance");
        var credits = new List<LlmCreditBalance>
        {
            new() { Id = "prepaid", DisplayName = "Prepaid credits", Remaining = prepaid, Unit = "usd", HasCredits = prepaid > 0 }
        };
        if (Boolean(root, "onDemandEnabled") == true)
        {
            decimal onDemand = Math.Max(Cents(config, "onDemandCap") - Cents(config, "onDemandUsed"), 0m);
            credits.Add(new() { Id = "on-demand", DisplayName = "On-demand credits", Remaining = onDemand, Unit = "usd", HasCredits = onDemand > 0 });
        }
        return new()
        {
            RetrievedAt = retrievedAt,
            Plan = String(root, "subscriptionTier", "subscriptionTier"),
            Quotas =
            [
                new()
                {
                    Id = "grok:" + id,
                    DisplayName = name,
                    UsedFraction = used,
                    RemainingFraction = 1d - used,
                    WindowDuration = end - start,
                    ResetsAt = end,
                    State = used >= 1d ? LlmQuotaState.Exhausted : LlmQuotaState.Available
                }
            ],
            Credits = credits
        };
    }

    /// <summary>A cent amount (<c>{"val": n}</c>) in dollars; absent or without val is $0.</summary>
    private static decimal Cents(JsonElement config, string key)
    {
        string path = "config." + key;
        if (Object(config, key, path) is not { } cent || !cent.TryGetProperty("val", out JsonElement value)) return 0m;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long cents)
            ? cents / 100m : throw Unrecognized(path + ".val", "is not a whole number");
    }

    private static DateTimeOffset Time(JsonElement root, string key, string path) =>
        String(root, key, path) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset time)
            ? time : throw Unrecognized(path, "is missing or not a timestamp");

    // Each reader returns null when the field is absent and throws when it is present with another kind.
    private static JsonElement? Object(JsonElement root, string key, string path) =>
        Read(root, key, path, JsonValueKind.Object, "an object") is { } value ? value : null;
    private static string? String(JsonElement root, string key, string path) =>
        Read(root, key, path, JsonValueKind.String, "a string")?.GetString();
    private static double? Number(JsonElement root, string key, string path) =>
        Read(root, key, path, JsonValueKind.Number, "a number")?.GetDouble();
    private static bool? Boolean(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw Unrecognized(key, "is not a boolean")
            }
            : null;

    private static JsonElement? Read(JsonElement root, string key, string path, JsonValueKind kind, string expected) =>
        !root.TryGetProperty(key, out JsonElement value) ? null
        : value.ValueKind == kind ? value
        : throw Unrecognized(path, "is not " + expected);

    private static FormatException Unrecognized(string path, string problem) => new($"{path} {problem}");
}
