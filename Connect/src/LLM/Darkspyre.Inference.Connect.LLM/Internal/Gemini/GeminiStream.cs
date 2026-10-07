using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>The route-specific parts of a Gemini stream: naming, status checks and continuation ownership.</summary>
internal sealed record GeminiRoute(LlmTransport Transport, string Name, string CallIdPrefix,
    Func<HttpResponseMessage, CancellationToken, Task> CheckStatusAsync, Func<LlmConnection, string, JsonArray, LlmContinuationState> Encode);

/// <summary>
/// Reads a Gemini generateContent response or streamGenerateContent SSE stream. The caller supplies the URL and
/// authentication.
/// </summary>
internal static class GeminiStream
{
    public static async IAsyncEnumerable<LlmStreamEvent> ReadAsync(HttpClient http, HttpRequestMessage message, GeminiRoute route,
        LlmConnection connection, LlmChatRequest request, LlmStructuredOutputEnforcement? enforcement, bool stream, bool usageDeltas, bool reasoningText,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using HttpRequestMessage owned = message;
        using var consumption = SdkHttpConsumption.Enter(http, cancellationToken); cancellationToken = consumption.Token;
        using HttpResponseMessage response = await http.SendAsync(owned, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await route.CheckStatusAsync(response, cancellationToken).ConfigureAwait(false);
        consumption.Fence();
        LlmProviderException Failure(string reason) => new(reason, route.Transport);
        var text = new StringBuilder();
        var parts = new JsonArray();
        var calls = new List<LlmToolCall>();
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        bool invalidCall = false;
        var argumentStream = new GeminiArgumentStream();
        bool streamedArguments = route.Transport == LlmTransport.GoogleVertexAi && stream && request.Tools.Count > 0
            && GeminiModelProfiles.Get(request.ModelId[(request.ModelId.LastIndexOf('/') + 1)..], null).StreamedFunctionArguments;
        string? finish = null;
        string? blocked = null;
        string? responseId = null;
        string? observedModel = null;
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        int? prompt = null, toolPrompt = null, visibleOutput = null, thoughts = null, cached = null, total = null;
        var deltas = new UsageDeltas(stream && usageDeltas);
        // A generateContent response is one GenerateContentResponse, the same type as each streamed chunk.
        IAsyncEnumerable<string> payloads = stream
            ? ReadStreamAsync(response, cancellationToken)
            : ReadCompleteAsync(response, cancellationToken);
        await foreach (string data in payloads.ConfigureAwait(false))
        {
            using JsonDocument document = JsonDocument.Parse(data);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out _))
            {
                throw new LlmProviderException($"{route.Name} returned a stream error.", route.Transport, response.StatusCode,
                    retryAfter: response.Headers.RetryAfter?.Delta,
                    isContextOverflow: LlmContextOverflow.Google(root));
            }
            responseId = LlmJson.GetString(root, "responseId") ?? responseId;
            observedModel = ObservedModel.Read(root, "modelVersion", route.Transport, connection, credential,
                owned, observedModel);
            if (LlmJson.GetObject(root, "usageMetadata") is { } usage)
            {
                prompt = LlmJson.GetInt(usage, "promptTokenCount");
                toolPrompt = LlmJson.GetInt(usage, "toolUsePromptTokenCount");
                visibleOutput = LlmJson.GetInt(usage, "candidatesTokenCount");
                thoughts = LlmJson.GetInt(usage, "thoughtsTokenCount");
                cached = LlmJson.GetInt(usage, "cachedContentTokenCount");
                total = LlmJson.GetInt(usage, "totalTokenCount");
                if (deltas.Next(Usage(Input(), visibleOutput, thoughts, cached, total)) is { } observation)
                {
                    yield return consumption.Publish<LlmStreamEvent>(observation);
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
                    cancellationToken.ThrowIfCancellationRequested();
                    JsonObject part = JsonNode.Parse(item.GetRawText())!.AsObject();
                    if (part.Any(field => field.Key is not ("text" or "thought" or "thoughtSignature" or "functionCall")))
                    { throw Failure($"{route.Name} returned unsupported assistant content."); }
                    if (part["text"] is { } value)
                    {
                        string delta = value.GetValue<string>();
                        if ((bool?)part["thought"] == true)
                        { if (reasoningText) { yield return consumption.Publish<LlmStreamEvent>(new LlmReasoningDeltaEvent(delta, true)); } }
                        else { text.Append(delta); yield return consumption.Publish<LlmStreamEvent>(new LlmTextDeltaEvent(delta)); }
                    }
                    if (part["functionCall"] is JsonObject function)
                    {
                        if (finish is "MALFORMED_FUNCTION_CALL" or "UNEXPECTED_TOOL_CALL" or "TOO_MANY_TOOL_CALLS") { continue; }
                        if (argumentStream.IsOpen || function.ContainsKey("partialArgs") || function.ContainsKey("willContinue") || function.Count == 0)
                        {
                            if (!streamedArguments || part.Any(field => field.Key is not ("functionCall" or "thoughtSignature")))
                            { throw Failure($"{route.Name} returned unexpected streamed function arguments."); }
                            var observation = argumentStream.Observe(part, () => route.CallIdPrefix + Guid.NewGuid().ToString("N"), cancellationToken);
                            if (observation.Started)
                            {
                                string streamedId = (string)observation.Part["connectCallId"]!;
                                string streamedName = (string)observation.Part["functionCall"]!["name"]!;
                                if (!callIds.Add(streamedId)) { throw Failure($"{route.Name} returned a duplicate function identity."); }
                                parts.Add(item: observation.Part);
                                yield return consumption.Publish<LlmStreamEvent>(new LlmToolCallStartedEvent(streamedId, streamedName, calls.Count));
                            }
                            if (observation.Completed is { } completed) { calls.Add(completed); }
                            continue;
                        }
                        string? name = function["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out string? foundName) ? foundName : null;
                        if (string.IsNullOrWhiteSpace(name) || GeminiContinuation.EffectiveArguments(function) is not JsonObject arguments)
                        {
                            invalidCall = true;
                            continue;
                        }
                        string id = (string?)function["id"] ?? route.CallIdPrefix + Guid.NewGuid().ToString("N");
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !callIds.Add(id))
                        { throw Failure($"{route.Name} returned an invalid or duplicate function identity."); }
                        part["connectCallId"] = id;
                        calls.Add(new(id, name, arguments.ToJsonString()));
                        yield return consumption.Publish<LlmStreamEvent>(new LlmToolCallStartedEvent(id, name, calls.Count - 1));
                    }
                    parts.Add(item: part);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (finish is null && blocked is null) { throw Failure($"{route.Name} ended without a terminal reason."); }
        if (finish == "MISSING_THOUGHT_SIGNATURE") { throw Failure($"{route.Name} rejected a tool turn without its thought signature."); }
        bool failed = finish is "MALFORMED_FUNCTION_CALL" or "UNEXPECTED_TOOL_CALL" or "TOO_MANY_TOOL_CALLS";
        if (invalidCall && !failed) { throw Failure($"{route.Name} returned an invalid function call."); }
        bool refused = blocked is not null || finish is "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "MODEL_ARMOR" or "IMAGE_SAFETY";
        LlmCompletionStatus status = failed ? LlmCompletionStatus.Failed : refused ? LlmCompletionStatus.Refused
            : finish == "MAX_TOKENS" ? LlmCompletionStatus.Incomplete
            : finish == "STOP" ? calls.Count > 0 ? LlmCompletionStatus.RequiresAction : LlmCompletionStatus.Completed
            : LlmCompletionStatus.Unknown;
        if (argumentStream.IsOpen && status is LlmCompletionStatus.Completed or LlmCompletionStatus.RequiresAction)
        { throw Failure($"{route.Name} returned incomplete function arguments."); }
        LlmToolCall[] completeCalls = status is LlmCompletionStatus.RequiresAction or LlmCompletionStatus.Unknown ? calls.ToArray() : [];
        foreach (LlmToolCall call in completeCalls) { yield return consumption.Publish<LlmStreamEvent>(new LlmToolCallCompletedEvent(call)); }
        int? input = Input();
        LlmUsage? counts = Usage(input, visibleOutput, thoughts, cached, total);
        LlmContinuationState? state = status is LlmCompletionStatus.Completed or LlmCompletionStatus.RequiresAction
            ? route.Encode(connection, request.ModelId, parts) with { InputTokens = input, OutputTokens = counts?.OutputTokens } : null;
        yield return consumption.Publish<LlmStreamEvent>(new LlmChatCompletionEvent(new()
        {
            Text = text.ToString(),
            ModelId = observedModel,
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
        }));

        int? Input() => prompt is int known ? checked(known + (toolPrompt ?? 0)) : null;
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
        await using Stream body = await SdkHttpConsumption.ReadAsStreamAsync(response.Content, cancellationToken).ConfigureAwait(false);
        await foreach (string data in Sse.ReadDataPayloadsAsync(body, cancellationToken).ConfigureAwait(false))
        {
            yield return SdkHttpConsumption.Publish(response.Content, data, cancellationToken);
        }
    }

    private static async IAsyncEnumerable<string> ReadCompleteAsync(HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return SdkHttpConsumption.Publish(response.Content, await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false), cancellationToken);
    }

    private static string? SafeReason(string? reason) => reason is null ? null
        : reason.Length <= 64 && reason.All(character => char.IsAsciiLetterUpper(character) || character == '_') ? reason : "UNKNOWN";
}
