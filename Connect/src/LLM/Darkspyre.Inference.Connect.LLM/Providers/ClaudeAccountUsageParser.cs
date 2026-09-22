using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal static partial class ClaudeAccountUsageParser
{
    private const int MaximumSummaryCharacters = 64 * 1024;

    public static LlmAccountUsageSnapshot Parse(
        string summary,
        DateTimeOffset retrievedAt)
    {
        string normalized = summary.ReplaceLineEndings("\n").Trim();
        if (normalized.Length > MaximumSummaryCharacters)
        {
            throw new FormatException("Claude CLI account usage exceeded its safety limit.");
        }

        var quotas = new List<LlmQuotaWindow>();
        var metrics = new List<LlmUsageMetric>();
        foreach (string line in normalized.Split('\n', StringSplitOptions.TrimEntries))
        {
            Match quota = QuotaLine().Match(line);
            if (quota.Success
                && double.TryParse(
                    quota.Groups["used"].Value,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out double usedPercent))
            {
                string label = quota.Groups["label"].Value.Trim();
                double usedFraction = Math.Clamp(usedPercent / 100d, 0d, 1d);
                quotas.Add(new LlmQuotaWindow
                {
                    Id = Slug(label),
                    DisplayName = label,
                    MeterId = ReadMeterId(label),
                    UsedFraction = usedFraction,
                    RemainingFraction = Math.Clamp((100d - usedPercent) / 100d, 0d, 1d),
                    ResetsAt = ParseReset(quota.Groups["reset"].Value, retrievedAt),
                    State = usedFraction >= 1d
                        ? LlmQuotaState.Exhausted
                        : LlmQuotaState.Available
                });
                continue;
            }

            Match activity = ActivityLine().Match(line);
            if (!activity.Success
                || !decimal.TryParse(
                    activity.Groups["requests"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out decimal requests)
                || !decimal.TryParse(
                    activity.Groups["sessions"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out decimal sessions))
            {
                continue;
            }

            string periodLabel = activity.Groups["period"].Value;
            TimeSpan? period = ParseDuration(periodLabel);
            string periodId = Slug(periodLabel);
            metrics.Add(new LlmUsageMetric
            {
                Id = $"requests:{periodId}",
                DisplayName = $"Requests ({periodLabel})",
                Value = requests,
                Unit = "requests",
                PeriodDuration = period,
                IsApproximate = true
            });
            metrics.Add(new LlmUsageMetric
            {
                Id = $"sessions:{periodId}",
                DisplayName = $"Sessions ({periodLabel})",
                Value = sessions,
                Unit = "sessions",
                PeriodDuration = period,
                IsApproximate = true
            });
        }

        if (quotas.Count == 0)
        {
            throw new FormatException("Claude CLI account usage did not contain any quota windows.");
        }

        return new LlmAccountUsageSnapshot
        {
            RetrievedAt = retrievedAt,
            Summary = normalized,
            Quotas = quotas,
            Metrics = metrics
        };
    }

    private static string? ReadMeterId(string label)
    {
        int start = label.LastIndexOf('(');
        if (start < 0 || !label.EndsWith(')'))
        {
            return null;
        }

        string value = label[(start + 1)..^1].Trim();
        return value.Equals("all models", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    private static DateTimeOffset? ParseReset(string value, DateTimeOffset retrievedAt)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        string? timeZoneId = null;
        int zoneStart = trimmed.LastIndexOf('(');
        if (zoneStart >= 0 && trimmed.EndsWith(')'))
        {
            timeZoneId = trimmed[(zoneStart + 1)..^1].Trim();
            trimmed = trimmed[..zoneStart].Trim();
        }

        TimeZoneInfo zone;
        try
        {
            zone = string.IsNullOrWhiteSpace(timeZoneId)
                ? TimeZoneInfo.Local
                : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }

        DateTime localNow = TimeZoneInfo.ConvertTime(retrievedAt, zone).DateTime;
        bool containsYear = Year().IsMatch(trimmed);
        string[] formats = containsYear
            ? ["MMM d, yyyy 'at' h:mmtt", "MMM d, yyyy 'at' htt"]
            : ["MMM d 'at' h:mmtt", "MMM d 'at' htt"];
        if (!DateTime.TryParseExact(
                trimmed,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTime parsed))
        {
            return null;
        }

        if (!containsYear)
        {
            parsed = new DateTime(
                localNow.Year,
                parsed.Month,
                parsed.Day,
                parsed.Hour,
                parsed.Minute,
                parsed.Second,
                DateTimeKind.Unspecified);
            if (parsed < localNow.AddDays(-1))
            {
                parsed = parsed.AddYears(1);
            }
        }

        parsed = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        return new DateTimeOffset(parsed, zone.GetUtcOffset(parsed)).ToUniversalTime();
    }

    private static TimeSpan? ParseDuration(string value)
    {
        if (value.EndsWith('h')
            && double.TryParse(
                value[..^1],
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out double hours))
        {
            return TimeSpan.FromHours(hours);
        }

        if (value.EndsWith('d')
            && double.TryParse(
                value[..^1],
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out double days))
        {
            return TimeSpan.FromDays(days);
        }

        return null;
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

    [GeneratedRegex(
        @"^(?<label>[^:]+):\s*(?<used>\d+(?:\.\d+)?)%\s+used(?:\s+[·\-–—]\s+resets\s+(?<reset>.+))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QuotaLine();

    [GeneratedRegex(
        @"^Last\s+(?<period>\d+(?:\.\d+)?[hd])\s+[·\-–—]\s+(?<requests>\d+)\s+requests\s+[·\-–—]\s+(?<sessions>\d+)\s+sessions$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ActivityLine();

    [GeneratedRegex(@"\b\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Year();
}
