using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>The route-specific parts of a Gemini stream: naming, status checks and continuation ownership.</summary>
internal sealed record GeminiRoute(LlmTransport Transport, string Name, string CallIdPrefix,
    Action<HttpResponseMessage> CheckStatus, Func<LlmConnection, string, JsonArray, LlmContinuationState> Encode);

/// <summary>
/// Reads a Gemini generateContent response or streamGenerateContent SSE stream. The caller supplies the URL and
/// authentication.
/// </summary>
internal static class GeminiStream
{
    public static async IAsyncEnumerable<LlmStreamEvent> ReadAsync(HttpClient http, HttpRequestMessage message, GeminiRoute route,
        LlmConnection connection, LlmChatRequest request, LlmStructuredOutputEnforcement? enforcement, bool stream, bool usageDeltas,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using HttpRequestMessage owned = message;
        using HttpResponseMessage response = await http.SendAsync(owned, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        route.CheckStatus(response);
        LlmProviderException Failure(string reason) => new(reason, route.Transport);
        var text = new StringBuilder();
        var parts = new JsonArray();
        var calls = new List<LlmToolCall>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        string? finish = null;
        string? blocked = null;
        string? responseId = null;
        int? input = null, visibleOutput = null, thoughts = null, cached = null, total = null;
        var deltas = new UsageDeltas(stream && usageDeltas);
        // A generateContent response is one GenerateContentResponse, the same type as each streamed chunk.
        IAsyncEnumerable<string> payloads = stream
            ? ReadStreamAsync(response, cancellationToken)
            : ReadCompleteAsync(response, cancellationToken);
        await foreach (string data in payloads.ConfigureAwait(false))
        {
            using JsonDocument document = JsonDocument.Parse(data);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out _)) { throw Failure($"{route.Name} returned a stream error."); }
            responseId = LlmJson.GetString(root, "responseId") ?? responseId;
            if (LlmJson.GetObject(root, "usageMetadata") is { } usage)
            {
                input = LlmJson.GetInt(usage, "promptTokenCount") ?? input;
                visibleOutput = LlmJson.GetInt(usage, "candidatesTokenCount") ?? visibleOutput;
                thoughts = LlmJson.GetInt(usage, "thoughtsTokenCount") ?? thoughts;
                cached = LlmJson.GetInt(usage, "cachedContentTokenCount") ?? cached;
                total = LlmJson.GetInt(usage, "totalTokenCount") ?? total;
                if (deltas.Next(Usage(input, visibleOutput, thoughts, cached, total)) is { } observation)
                {
                    yield return observation;
                }
            }
            if (LlmJson.GetObject(root, "promptFeedback") is { } feedback)
            { blocked = LlmJson.GetString(feedback, "blockReason") ?? blocked; }
            if (!root.TryGetProperty("candidates", out JsonElement candidates)) { continue; }
            if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() > 1)
            { throw Failure($"{route.Name} returned an invalid candidate count."); }
            foreach (JsonElement candidate in candidates.EnumerateArray())
            {
                if (LlmJson.GetInt(candidate, "index") is int index && index != 0)
                { throw Failure($"{route.Name} returned an unexpected candidate index."); }
                string? nextFinish = LlmJson.GetString(candidate, "finishReason");
                if (nextFinish is not null)
                {
                    if (finish is not null && finish != nextFinish) { throw Failure($"{route.Name} returned conflicting terminal reasons."); }
                    finish = nextFinish;
                }
                if (LlmJson.GetObject(candidate, "content") is not { } content
                    || !content.TryGetProperty("parts", out JsonElement received)) { continue; }
                foreach (JsonElement item in received.EnumerateArray())
                {
                    JsonObject part = JsonNode.Parse(item.GetRawText())!.AsObject();
                    if (part.Any(field => field.Key is not ("text" or "thought" or "thoughtSignature" or "functionCall")))
                    { throw Failure($"{route.Name} returned unsupported assistant content."); }
                    if (part["text"] is { } value)
                    {
                        string delta = value.GetValue<string>();
                        if ((bool?)part["thought"] == true) { yield return new LlmReasoningDeltaEvent(delta, true); }
                        else { text.Append(delta); yield return new LlmTextDeltaEvent(delta); }
                    }
                    if (part["functionCall"] is JsonObject function)
                    {
                        string name = (string?)function["name"] ?? throw Failure($"{route.Name} returned an unnamed function call.");
                        if (function["args"] is not JsonObject arguments) { throw Failure($"{route.Name} returned incomplete function arguments."); }
                        string id = (string?)function["id"] ?? route.CallIdPrefix + Guid.NewGuid().ToString("N");
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !callIds.Add(id))
                        { throw Failure($"{route.Name} returned an invalid or duplicate function identity."); }
                        part["connectCallId"] = id;
                        calls.Add(new(id, name, arguments.ToJsonString()));
                        yield return new LlmToolCallStartedEvent(id, name, calls.Count - 1);
                    }
                    parts.Add(part);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (finish is null && blocked is null) { throw Failure($"{route.Name} ended without a terminal reason."); }
        if (finish is "MALFORMED_FUNCTION_CALL" or "UNEXPECTED_TOOL_CALL") { throw Failure($"{route.Name} could not produce a valid function call."); }
        bool refused = blocked is not null || finish is "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "MODEL_ARMOR";
        LlmCompletionStatus status = refused ? LlmCompletionStatus.Refused
            : finish == "MAX_TOKENS" ? LlmCompletionStatus.Incomplete
            : finish == "STOP" ? calls.Count > 0 ? LlmCompletionStatus.RequiresAction : LlmCompletionStatus.Completed
            : LlmCompletionStatus.Unknown;
        LlmToolCall[] completeCalls = status == LlmCompletionStatus.RequiresAction ? calls.ToArray() : [];
        foreach (LlmToolCall call in completeCalls) { yield return new LlmToolCallCompletedEvent(call); }
        LlmUsage? counts = Usage(input, visibleOutput, thoughts, cached, total);
        LlmContinuationState? state = status is LlmCompletionStatus.Completed or LlmCompletionStatus.RequiresAction
            ? route.Encode(connection, request.ModelId, parts) with { InputTokens = input, OutputTokens = counts?.OutputTokens } : null;
        yield return new LlmChatCompletionEvent(new()
        {
            Text = text.ToString(),
            Status = status,
            ToolCalls = completeCalls,
            ContinuationState = state,
            FinishReason = SafeReason(finish),
            IncompleteReason = status == LlmCompletionStatus.Incomplete ? "MAX_TOKENS" : null,
            Refusal = refused ? "The provider declined this request." : null,
            RefusalCategory = refused ? SafeReason(blocked ?? finish) : null,
            Usage = counts,
            ProviderResponseId = responseId,
            StructuredOutput = request.StructuredOutput is { } schema && enforcement is { } mode && status == LlmCompletionStatus.Completed
                ? StructuredOutput.Validate(text.ToString(), schema, mode) : null,
            Diagnostics = new((int)response.StatusCode, null, null)
        });
    }

    private static LlmUsage? Usage(int? input, int? visibleOutput, int? thoughts, int? cached, int? total)
    {
        int? output = visibleOutput is int visible ? checked(visible + (thoughts ?? 0)) : null;
        return input is null && output is null && total is null && cached is null && thoughts is null ? null
            : new(input, output, total) { CachedInputTokens = cached, ReasoningOutputTokens = thoughts };
    }

    private static async IAsyncEnumerable<string> ReadStreamAsync(HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (string data in Sse.ReadDataPayloadsAsync(body, cancellationToken).ConfigureAwait(false))
        {
            yield return data;
        }
    }

    private static async IAsyncEnumerable<string> ReadCompleteAsync(HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? SafeReason(string? reason) => reason is null ? null
        : reason.Length <= 64 && reason.All(character => char.IsAsciiLetterUpper(character) || character == '_') ? reason : "UNKNOWN";
}
