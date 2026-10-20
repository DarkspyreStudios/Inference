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
                    WindowDuration = quota.Groups["period"].Value.Equals("session", StringComparison.OrdinalIgnoreCase)
                        ? TimeSpan.FromHours(5)
                        : TimeSpan.FromDays(7),
                    UsedFraction = usedFraction,
                    RemainingFraction = Math.Clamp((100d - usedPercent) / 100d, 0d, 1d),
                    ResetsAt = ParseReset(quota.Groups["reset"].Value, retrievedAt),
                    State = usedFraction >= 1d
                        ? LlmQuotaState.Exhausted
                        : LlmQuotaState.Available
                });
            }
        }

        if (quotas.Count == 0)
        {
            string firstLine = normalized.Split('\n', 2)[0];
            if (firstLine.Length > 200) firstLine = firstLine[..200];
            throw new FormatException("Claude /usage reported no quota windows. First result line: " + firstLine);
        }

        return new LlmAccountUsageSnapshot
        {
            RetrievedAt = retrievedAt,
            Summary = normalized,
            Quotas = quotas
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
        @"^(?<label>Current\s+(?<period>session|week)(?:\s+\([^)]+\))?):\s*(?<used>\d+(?:\.\d+)?)%\s+used(?:\s+[·\-–—]\s+resets\s+(?<reset>.+))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QuotaLine();

    [GeneratedRegex(@"\b\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Year();
}
