using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Logs the shape of each Claude CLI <c>rate_limit_event</c>: the names of the fields its <c>rate_limit_info</c> object
/// holds, in the order the CLI sent them, and the object's length in characters. No field's value is logged, because the
/// object's shape belongs to the CLI and a field it gains later could name an account or an organization
/// (DSA-844, Brandon 2026-10-29). An event is logged only when the object differs from the previous one for the same
/// connection and <c>rateLimitType</c>, and nothing else of the stream is logged.
/// </summary>
/// <gotcha>
/// The <c>rateLimitType</c> stays in the line, because it says which limit window the line is about and the log keeps one
/// line per window. It is the limit window's id, such as <c>five_hour</c>, which the application also shows as a quota id.
/// </gotcha>
internal sealed partial class ClaudeRateLimitLog(ILogger<ClaudeRateLimitLog> logger)
{
    private readonly ConcurrentDictionary<(string ConnectionId, string RateLimitType), string> _last = new();

    internal void Record(LlmConnection connection, JsonElement info)
    {
        if (info.ValueKind != JsonValueKind.Object) { return; }
        string raw = info.GetRawText();
        string type = info.TryGetProperty("rateLimitType", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : string.Empty;
        string connectionId = connection.AccountUsageObservationBinding?.ConnectionId ?? string.Empty;
        if (_last.TryGetValue((connectionId, type), out string? previous) && previous == raw) { return; }
        _last[(connectionId, type)] = raw;
        // Reading the field names walks the object, so it happens only when the line is actually written.
        if (!logger.IsEnabled(LogLevel.Information)) { return; }
        string fields = FieldNames(info);
        LogRateLimitEvent(logger, connectionId, type, fields, raw.Length);
    }

    /// <summary>The object's field names in the order received, or "none" for an object with no field.</summary>
    private static string FieldNames(JsonElement info)
    {
        List<string> names = [.. info.EnumerateObject().Select(field => field.Name)];
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Claude rate_limit_event on {ConnectionId} ({RateLimitType}): fields {FieldNames}, {Characters} characters")]
    private static partial void LogRateLimitEvent(ILogger logger, string connectionId, string rateLimitType, string fieldNames,
        int characters);
}
