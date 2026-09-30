using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>One allowlisted output event of the Antigravity CLI stream-json protocol.</summary>
internal abstract record AntigravityEvent;

/// <summary>The session handshake. It arrives before any prompt is read.</summary>
internal sealed record AntigravityInit(string ConversationId) : AntigravityEvent;

/// <summary>A step snapshot. A step is reported ACTIVE while it runs and DONE once.</summary>
internal sealed record AntigravityStep(
    string? ConversationId,
    int StepIndex,
    string State,
    string StepType,
    string? TextDelta,
    AntigravityToolStep? Tool,
    AntigravityUsage? Usage) : AntigravityEvent
{
    public bool IsDone => string.Equals(State, "DONE", StringComparison.Ordinal);
}

/// <summary>A native tool observation. Output and error are the provider's report, not host results.</summary>
internal sealed record AntigravityToolStep(
    string Name,
    JsonElement Parameters,
    string? Output,
    string? ErrorMessage);

/// <summary>The terminal event of one turn.</summary>
internal sealed record AntigravityResult(
    string? ConversationId,
    string Status,
    string Response,
    JsonElement? StructuredOutput,
    AntigravityUsage? Usage) : AntigravityEvent;

/// <summary>An event name this adapter does not interpret.</summary>
internal sealed record AntigravityUnrecognizedEvent : AntigravityEvent;

/// <summary>
/// Counters as reported. Result counters are cumulative for the whole conversation. A null counter was
/// absent; <see cref="Invalid"/> is true when a present counter was negative, fractional or too large.
/// </summary>
internal sealed record AntigravityUsage(
    long? InputTokens,
    long? OutputTokens,
    long? ThinkingTokens,
    long? CacheReadTokens,
    long? TotalTokens,
    bool Invalid);

/// <summary>Parses the stream-json protocol once for the chat and agent paths.</summary>
internal static class AntigravityProtocol
{
    private const int MaximumConversationIdLength = 128;

    public static AntigravityEvent Parse(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Malformed(null);
            }
            return String(root, "event") switch
            {
                "init" => new AntigravityInit(
                    ConversationId(String(root, "conversation_id"))
                    ?? throw Malformed(null)),
                "step_update" when root.TryGetProperty("step_update", out JsonElement step)
                    && step.ValueKind == JsonValueKind.Object => ParseStep(step),
                "result" when root.TryGetProperty("result", out JsonElement result)
                    && result.ValueKind == JsonValueKind.Object => ParseResult(result),
                "step_update" or "result" => throw Malformed(null),
                _ => new AntigravityUnrecognizedEvent()
            };
        }
        catch (JsonException exception)
        {
            throw Malformed(exception);
        }
    }

    /// <summary>
    /// Parses the single JSON object that <c>--output-format json</c> writes. It has the shape of the stream's
    /// <c>result</c> payload.
    /// </summary>
    public static AntigravityResult ParseResultEnvelope(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ParseResult(document.RootElement)
                : throw Malformed(null);
        }
        catch (JsonException exception)
        {
            throw Malformed(exception);
        }
    }

    /// <summary>A valid opaque conversation identifier, or null. It is never used as a path or shell text.</summary>
    public static string? ConversationId(string? value) =>
        value is { Length: > 0 and <= MaximumConversationIdLength }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : null;

    private static AntigravityStep ParseStep(JsonElement step)
    {
        string? state = String(step, "state");
        string? stepType = String(step, "step_type");
        if (!step.TryGetProperty("step_index", out JsonElement indexElement)
            || !indexElement.TryGetInt32(out int stepIndex)
            || stepIndex < 0
            || state is not ("ACTIVE" or "DONE")
            || string.IsNullOrEmpty(stepType))
        {
            throw Malformed(null);
        }

        AntigravityToolStep? tool = null;
        if (string.Equals(stepType, "tool", StringComparison.Ordinal))
        {
            JsonElement info = step.TryGetProperty("tool_info", out JsonElement toolInfo)
                && toolInfo.ValueKind == JsonValueKind.Object
                    ? toolInfo
                    : default;
            string name = (info.ValueKind == JsonValueKind.Object ? String(info, "name") : null)
                ?? String(step, "tool_name")
                ?? throw Malformed(null);
            JsonElement parameters = info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("parameters", out JsonElement parameterElement)
                && parameterElement.ValueKind == JsonValueKind.Object
                    ? parameterElement.Clone()
                    : JsonSerializer.SerializeToElement(new { });
            string? output = info.ValueKind == JsonValueKind.Object ? String(info, "output") : null;
            string? error = null;
            JsonElement errorSource = info.ValueKind == JsonValueKind.Object ? info : step;
            if (errorSource.TryGetProperty("error", out JsonElement errorElement)
                && errorElement.ValueKind == JsonValueKind.Object)
            {
                error = String(errorElement, "message") ?? "The native tool reported an error.";
            }
            tool = new AntigravityToolStep(name, parameters, output, error);
        }

        return new AntigravityStep(
            String(step, "conversation_id"),
            stepIndex,
            state,
            stepType,
            String(step, "text_delta"),
            tool,
            ParseUsage(step));
    }

    private static AntigravityResult ParseResult(JsonElement result)
    {
        JsonElement? structured = result.TryGetProperty("structured_output", out JsonElement structuredElement)
            && structuredElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
                ? structuredElement.Clone()
                : null;
        return new AntigravityResult(
            String(result, "conversation_id"),
            String(result, "status") ?? string.Empty,
            String(result, "response") ?? string.Empty,
            structured,
            ParseUsage(result));
    }

    private static AntigravityUsage? ParseUsage(JsonElement owner)
    {
        if (!owner.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        bool invalid = false;
        long? Counter(string name)
        {
            if (!usage.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long parsed) && parsed >= 0)
            {
                return parsed;
            }
            invalid = true;
            return null;
        }

        long? input = Counter("input_tokens");
        long? output = Counter("output_tokens");
        long? thinking = Counter("thinking_tokens");
        long? cacheRead = Counter("cache_read_tokens");
        long? total = Counter("total_tokens");
        return new AntigravityUsage(input, output, thinking, cacheRead, total, invalid);
    }

    private static string? String(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static LlmProviderException Malformed(Exception? innerException) => new(
        "Antigravity CLI returned malformed streaming JSON.",
        LlmTransport.GoogleAntigravityCli,
        providerCode: "antigravity_cli_invalid_json",
        innerException: innerException);
}
