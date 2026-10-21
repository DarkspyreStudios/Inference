using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>
/// Logs each Claude CLI <c>rate_limit_event</c>'s <c>rate_limit_info</c> object as received, so the fields the CLI sends
/// are visible. An event is logged only when it differs from the previous one for the same connection and
/// <c>rateLimitType</c>. The object carries limit status and reset times, never credentials; nothing else of the stream is logged.
/// </summary>
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
        LogRateLimitEvent(logger, connectionId, type, raw);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Claude rate_limit_event on {ConnectionId} ({RateLimitType}): {RateLimitInfo}")]
    private static partial void LogRateLimitEvent(ILogger logger, string connectionId, string rateLimitType, string rateLimitInfo);
}
