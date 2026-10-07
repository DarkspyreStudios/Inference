using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static partial class AntigravityAccountUsageParser
{
    private const int MaximumSummaryCharacters = 64 * 1024;

    public static LlmAccountUsageSnapshot Parse(
        string summary,
        DateTimeOffset retrievedAt)
    {
        string normalized = StripAnsi(summary).ReplaceLineEndings("\n").Trim();
        if (normalized.Length == 0 || normalized.Length > MaximumSummaryCharacters)
        {
            throw new FormatException("Antigravity CLI account usage was empty or exceeded its safety limit.");
        }

        if (TryParseStructured(normalized, retrievedAt, out LlmAccountUsageSnapshot structured))
        {
            return structured;
        }

        var quotas = new List<LlmQuotaWindow>();
        string? precedingLabel = null;
        string[] lines = normalized.Split('\n', StringSplitOptions.TrimEntries);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            Match percentage = Percentage().Match(line);
            if (percentage.Success
                && double.TryParse(
                    percentage.Groups["value"].Value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out double percent))
            {
                string label = CleanLabel(percentage.Groups["prefix"].Value);
                if (label.Length == 0)
                {
                    label = precedingLabel ?? "Model quota";
                }

                bool isRemaining = percentage.Groups["kind"].Value.Equals(
                    "remaining",
                    StringComparison.OrdinalIgnoreCase);
                double fraction = Math.Clamp(percent / 100d, 0d, 1d);
                double usedFraction = isRemaining
                    ? Math.Clamp((100d - percent) / 100d, 0d, 1d)
                    : fraction;
                double remainingFraction = isRemaining
                    ? fraction
                    : Math.Clamp((100d - percent) / 100d, 0d, 1d);
                string? resetText = percentage.Groups["reset"].Success
                    ? percentage.Groups["reset"].Value
                    : ReadReset(line);
                if (resetText is null && index + 1 < lines.Length)
                {
                    resetText = ReadReset(lines[index + 1]);
                }

                quotas.Add(new LlmQuotaWindow
                {
                    Id = UniqueId(quotas, Slug(label)),
                    DisplayName = label,
                    MeterId = Slug(label),
                    UsedFraction = usedFraction,
                    RemainingFraction = Math.Clamp(remainingFraction, 0d, 1d),
                    ResetsAt = ParseTimestamp(resetText),
                    State = remainingFraction <= 0d
                        ? LlmQuotaState.Exhausted
                        : LlmQuotaState.Available
                });
                continue;
            }

            Match absolute = AbsoluteRemaining().Match(line);
            if (absolute.Success
                && decimal.TryParse(
                    absolute.Groups["remaining"].Value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out decimal remaining))
            {
                string label = CleanLabel(absolute.Groups["prefix"].Value);
                if (label.Length == 0)
                {
                    label = precedingLabel ?? "Model quota";
                }

                string unit = absolute.Groups["unit"].Value.ToLowerInvariant().TrimEnd('s');
                quotas.Add(new LlmQuotaWindow
                {
                    Id = UniqueId(quotas, $"{Slug(label)}:{unit}"),
                    DisplayName = label,
                    MeterId = Slug(label),
                    Remaining = remaining,
                    Unit = $"{unit}s",
                    ResetsAt = ParseTimestamp(ReadReset(line)),
                    State = remaining <= 0m
                        ? LlmQuotaState.Exhausted
                        : LlmQuotaState.Available
                });
                continue;
            }

            if (!IsHeadingOrReset(line))
            {
                precedingLabel = CleanLabel(line);
            }
        }

        return new LlmAccountUsageSnapshot
        {
            RetrievedAt = retrievedAt,
            Summary = normalized,
            Quotas = quotas
        };
    }

    private static bool TryParseStructured(
        string value,
        DateTimeOffset retrievedAt,
        out LlmAccountUsageSnapshot snapshot)
    {
        snapshot = null!;
        if (!value.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("quota", out JsonElement quota)
                || quota.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var quotas = new List<LlmQuotaWindow>();
            foreach (JsonProperty entry in quota.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                double? remainingFraction = ReadDouble(entry.Value, "remaining_fraction");
                DateTimeOffset? resetsAt = entry.Value.TryGetProperty(
                        "reset_time",
                        out JsonElement resetTime)
                    && resetTime.ValueKind == JsonValueKind.String
                        ? ParseTimestamp(resetTime.GetString())
                        : null;
                quotas.Add(new LlmQuotaWindow
                {
                    Id = entry.Name,
                    DisplayName = entry.Name,
                    MeterId = entry.Name,
                    UsedFraction = remainingFraction is null
                        ? null
                        : Math.Clamp(1d - remainingFraction.Value, 0d, 1d),
                    RemainingFraction = remainingFraction is null
                        ? null
                        : Math.Clamp(remainingFraction.Value, 0d, 1d),
                    ResetsAt = resetsAt,
                    State = remainingFraction is null
                        ? LlmQuotaState.Unknown
                        : remainingFraction <= 0d
                            ? LlmQuotaState.Exhausted
                            : LlmQuotaState.Available
                });
            }

            snapshot = new LlmAccountUsageSnapshot
            {
                RetrievedAt = retrievedAt,
                Plan = root.TryGetProperty("plan_tier", out JsonElement plan)
                    && plan.ValueKind == JsonValueKind.String
                        ? plan.GetString()
                        : null,
                Quotas = quotas
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double? ReadDouble(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value)
        && value.TryGetDouble(out double parsed)
            ? parsed
            : null;

    private static string? ReadReset(string line)
    {
        Match match = Reset().Match(line);
        return match.Success ? match.Groups["reset"].Value.Trim() : null;
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed)
                ? parsed.ToUniversalTime()
                : null;

    private static bool IsHeadingOrReset(string line) =>
        line.Contains("quota", StringComparison.OrdinalIgnoreCase)
        || line.Contains("usage", StringComparison.OrdinalIgnoreCase)
        || Reset().IsMatch(line)
        || line.All(character => char.IsWhiteSpace(character)
            || char.IsPunctuation(character)
            || char.GetUnicodeCategory(character) == UnicodeCategory.OtherSymbol);

    private static string CleanLabel(string value) => value
        .Trim(' ', '\t', ':', '-', '–', '—', '·', '|', '[', ']', '(', ')')
        .Trim();

    private static string UniqueId(IReadOnlyList<LlmQuotaWindow> quotas, string candidate)
    {
        string id = candidate.Length == 0 ? "quota" : candidate;
        int suffix = 2;
        while (quotas.Any(quota => string.Equals(quota.Id, id, StringComparison.Ordinal)))
        {
            id = $"{candidate}-{suffix++}";
        }

        return id;
    }

    private static string Slug(string value)
    {
        var result = new StringBuilder(value.Length);
        bool separator = false;
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separator && result.Length > 0)
                {
                    result.Append('-');
                }
                result.Append(char.ToLowerInvariant(character));
                separator = false;
            }
            else
            {
                separator = true;
            }
        }

        return result.ToString();
    }

    private static string StripAnsi(string value) => AnsiEscape().Replace(value, string.Empty);

    [GeneratedRegex(
        @"^(?:(?<prefix>[^\t]+)\tWeekly Limit (?<kind>remaining)\t(?<value>\d+(?:\.\d+)?)%\t(?<reset>[^\t]+)$"
        + @"|(?<prefix>.*?)(?<value>\d+(?:\.\d+)?)\s*%\s*(?<kind>remaining|used)\b)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Percentage();

    [GeneratedRegex(
        @"^(?<prefix>.*?)(?<remaining>\d[\d,]*(?:\.\d+)?)\s+(?<unit>requests?|tokens?)\s+remaining\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteRemaining();

    [GeneratedRegex(
        @"\bresets?(?:\s+(?:at|in))?\s*[:=]?\s*(?<reset>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Reset();

    [GeneratedRegex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiEscape();
}
