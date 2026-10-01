using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Shared Responses projection, streaming and retained-output mapping.</summary>
internal abstract class ResponsesLlmClientBase(HttpClient http) : LlmClientBase(http), ILlmContextEstimator
{
    public string? EstimateDiagnostic => null;

    public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        ValidateSampling(request);
        var enforcement = request.StructuredOutput is null ? (LlmStructuredOutputEnforcement?)null
            : StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);
        JsonObject payload = BuildPayload(connection, request, enforcement, capabilities.SupportsStreaming);
        RequestJson.ApplyProviderOptions(payload, connection, request);

        // The provider's own count covers the history up to the last response that reported one, including
        // encrypted reasoning and attachments the local tokenizer cannot count. Only what follows is tokenized.
        int reported = -1;
        for (int index = request.Messages.Count - 1; index >= 0 && reported < 0; index--)
        {
            if (request.Messages[index].ContinuationState is { InputTokens: not null, OutputTokens: not null }) { reported = index; }
        }
        if (request.Messages.Skip(reported + 1).Any(Uncountable))
        {
            return null;
        }
        if (reported < 0)
        {
            // Tokenize the actual textual projection, including schemas/framing. This is an estimate:
            // server framing/current-model tokenization can differ from the bundled o200k encoding.
            var input = new JsonObject
            {
                ["input"] = payload["input"]?.DeepClone(),
                ["instructions"] = payload["instructions"]?.DeepClone(),
                ["tools"] = payload["tools"]?.DeepClone(),
                ["text"] = payload["text"]?.DeepClone()
            };
            return new ContextEstimate(LocalTokenCount.Count(input.ToJsonString()));
        }

        // Input items are built message by message, so the prefix's own projection marks where the rest begins.
        // The reported input already includes that request's instructions and tools.
        JsonArray full = (JsonArray)payload["input"]!;
        int prefixItems = ((JsonArray)BuildPayload(connection,
            request with { Messages = [.. request.Messages.Take(reported + 1)] }, enforcement,
            capabilities.SupportsStreaming)["input"]!).Count;
        var tail = new JsonArray([.. full.Skip(prefixItems).Select(item => item?.DeepClone())]);
        LlmContinuationState state = request.Messages[reported].ContinuationState!;
        return new ContextEstimate(state.InputTokens!.Value + state.OutputTokens!.Value
            + (tail.Count == 0 ? 0 : LocalTokenCount.Count(tail.ToJsonString())), IncludesProviderReport: true);
    }

    /// <summary>Content the local tokenizer cannot count: bytes, and encrypted reasoning.</summary>
    private static bool Uncountable(LlmChatMessage message) => message.Attachments.Count > 0
        || message.ToolResults.Any(result => result.Output.Parts.Any(part => part is LlmToolOutputBinary or LlmToolOutputResource { Data: not null }))
        || message.ContinuationState?.Payload.Contains("encrypted_content", StringComparison.Ordinal) == true;

    public override async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        ValidateSampling(request);
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is null
            ? null
            : StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);

        JsonObject payload = BuildPayload(connection, request, enforcement, capabilities.SupportsStreaming);
        RequestJson.ApplyProviderOptions(payload, connection, request);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            ProviderHttp.BuildUri(connection, "responses"))
        {
            Content = JsonContent.Create(payload)
        };
        ApplyRequestHeaders(httpRequest, connection, request);

        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        using HttpResponseMessage response = await Http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (ObserveAccountUsage(response) is { } observed) { yield return new AccountUsageObservedEvent(observed); }
        await ProviderHttp.ThrowIfUnsuccessfulAsync(
            response,
            Transport,
            credential,
            "OpenAI Responses request",
            cancellationToken);

        if (!capabilities.SupportsStreaming)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            foreach (LlmStreamEvent completeEvent in ReadCompleteResponse(
                         body, response, request, enforcement, credential, connection, capabilities.SupportsReasoningText))
            {
                yield return completeEvent;
            }

            yield break;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var text = new StringBuilder();
        var refusal = new StringBuilder();
        var toolCallStream = new OpenAiToolCallStream();
        var completedOutput = new SortedDictionary<int, JsonElement>();
        bool completed = false;

        await foreach (string data in Sse.ReadDataPayloadsAsync(stream, cancellationToken))
        {
            if (data == "[DONE]")
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(data);
            }
            catch (JsonException exception)
            {
                throw ProtocolException(
                    "OpenAI Responses returned a malformed stream event.",
                    response,
                    data,
                    credential,
                    exception);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                string? type = LlmJson.GetString(root, "type");
                switch (type)
                {
                    case "response.text.delta":
                    case "response.output_text.delta":
                        {
                            string delta = LlmJson.GetString(root, "delta") ?? string.Empty;
                            if (delta.Length > 0)
                            {
                                text.Append(delta);
                                yield return new LlmTextDeltaEvent(delta);
                            }

                            break;
                        }

                    case "response.reasoning_summary_text.delta":
                    case "response.reasoning_text.delta":
                        if (capabilities.SupportsReasoningText && LlmJson.GetString(root, "delta") is { Length: > 0 } reasoning)
                        {
                            yield return new LlmReasoningDeltaEvent(reasoning,
                                type == "response.reasoning_summary_text.delta");
                        }
                        break;
                    case "response.refusal.delta":
                        {
                            string delta = LlmJson.GetString(root, "delta") ?? string.Empty;
                            if (delta.Length > 0)
                            {
                                refusal.Append(delta);
                                yield return new LlmRefusalDeltaEvent(delta);
                            }

                            break;
                        }

                    case "response.output_item.added":
                        foreach (LlmStreamEvent toolEvent in toolCallStream.ObserveAdded(root))
                        {
                            yield return toolEvent;
                        }

                        break;

                    case "response.function_call_arguments.delta":
                        foreach (LlmStreamEvent toolEvent in toolCallStream.ObserveArgumentsDelta(root))
                        {
                            yield return toolEvent;
                        }

                        break;

                    case "response.output_item.done":
                        if (root.TryGetProperty("item", out var outputItem))
                        {
                            int index = LlmJson.GetInt(root, "output_index") ?? completedOutput.Count;
                            completedOutput[index] = outputItem.Clone();
                        }
                        foreach (LlmStreamEvent toolEvent in toolCallStream.ObserveDone(root))
                        {
                            yield return toolEvent;
                        }

                        break;

                    case "response.completed":
                    case "response.incomplete":
                    case "response.failed":
                    case "response.cancelled":
                        {
                            if (LlmJson.GetObject(root, "response") is { } failed
                                && LlmJson.GetObject(failed, "error") is { } error
                                && LlmJson.GetString(error, "code") == "context_length_exceeded")
                            {
                                throw StreamError(failed, response, data, credential);
                            }
                            LlmChatResult result = BuildResult(
                                CompleteOutput(root, completedOutput),
                                response,
                                data,
                                text.ToString(),
                                refusal.ToString(),
                                request.StructuredOutput,
                                enforcement,
                                credential,
                                connection,
                                request.ModelId);
                            foreach (LlmStreamEvent toolEvent in toolCallStream.CompleteMissing(result.ToolCalls))
                            {
                                yield return toolEvent;
                            }

                            completed = true;
                            yield return new LlmChatCompletionEvent(result);
                            break;
                        }

                    case "error":
                        throw StreamError(root, response, data, credential);
                }
            }

            if (completed)
            {
                yield break;
            }
        }

        throw ProtocolException(
            "OpenAI Responses ended without a terminal event.",
            response,
            string.Empty,
            credential);
    }

    protected virtual LlmAccountUsageSnapshot? ObserveAccountUsage(HttpResponseMessage response) =>
        OpenAiRateLimitHeaders.Parse(response.Headers, DateTimeOffset.UtcNow);

    /// <summary>OpenAI Responses applies its per-model sampling rule. Codex accepts no temperature at all.</summary>
    private void ValidateSampling(LlmChatRequest request)
    {
        if (Transport == LlmTransport.OpenAiResponses)
        {
            OpenAiModelProfiles.ValidateSampling(request);
        }
    }

    /// <summary>
    /// Maps a response that arrived as one Response object. Reasoning comes from the reasoning items in output order.
    /// </summary>
    private IEnumerable<LlmStreamEvent> ReadCompleteResponse(
        string body,
        HttpResponseMessage response,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        LlmCredential? credential,
        LlmConnection connection,
        bool reasoningText)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw ProtocolException(
                "OpenAI Responses returned a malformed response.",
                response,
                body,
                credential,
                exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw ProtocolException("OpenAI Responses returned a malformed response.", response, body, credential);
            }

            if (LlmJson.GetString(root, "status") == "failed"
                && LlmJson.GetObject(root, "error") is { } error
                && LlmJson.GetString(error, "code") == "context_length_exceeded")
            {
                throw StreamError(root, response, body, credential);
            }

            LlmChatResult result = BuildResult(
                root,
                response,
                body,
                string.Empty,
                string.Empty,
                request.StructuredOutput,
                enforcement,
                credential,
                connection,
                request.ModelId);
            return CompleteResponseEvents.From(result, reasoningText ? ReadReasoning(root) : []);
        }
    }

    private static List<LlmReasoningText> ReadReasoning(JsonElement root)
    {
        var reasoning = new List<LlmReasoningText>();
        if (!root.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array)
        {
            return reasoning;
        }

        foreach (JsonElement item in output.EnumerateArray())
        {
            if (LlmJson.GetString(item, "type") != "reasoning")
            {
                continue;
            }

            AddReasoning(item, "summary", "summary_text", isSummary: true);
            AddReasoning(item, "content", "reasoning_text", isSummary: false);
        }

        return reasoning;

        void AddReasoning(JsonElement item, string property, string partType, bool isSummary)
        {
            if (!item.TryGetProperty(property, out JsonElement parts) || parts.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (LlmJson.GetString(part, "type") == partType && LlmJson.GetString(part, "text") is { } text)
                {
                    reasoning.Add(new LlmReasoningText(text, isSummary));
                }
            }
        }
    }

    protected virtual JsonObject BuildPayload(
        LlmConnection connection,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        bool stream)
    {
        JsonArray input = OpenAiResponsesTools.BuildInput(
            request,
            connection,
            OpenAiResponsesTools.BuildMessage,
            output => ProjectToolOutput(connection, request.ModelId, output));

        var payload = new JsonObject
        {
            ["model"] = request.ModelId,
            ["input"] = input,
            ["stream"] = stream,
            ["store"] = false
        };

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            payload["instructions"] = request.Instructions;
        }

        if (request.Temperature is double temperature)
        {
            payload["temperature"] = temperature;
        }
        if (request.TopP is { } topP) { payload["top_p"] = topP; }

        if (request.MaxOutputTokens is int maxOutputTokens)
        {
            payload["max_output_tokens"] = maxOutputTokens;
        }

        if (request.ReasoningLevel is { } level)
        {
            payload["reasoning"] = new JsonObject { ["effort"] = LlmReasoningEffort.OpenAiWord(level) };
        }
        if (GetCapabilities(connection, request.ModelId).SupportsReasoningText
            && request.ReasoningLevel != LlmReasoningLevel.None
            && (request.ReasoningLevel is not null || ReasonsByDefault(connection, request.ModelId)))
        {
            payload["reasoning"] ??= new JsonObject();
            payload["reasoning"]!["summary"] = "auto";
        }

        // Stateless ordinary responses need their reasoning state just as tool responses do.
        payload["include"] = new JsonArray("reasoning.encrypted_content");
        if (request.Tools.Count > 0)
        {
            payload["tools"] = OpenAiResponsesTools.BuildTools(request.Tools);
        }

        if (request.ToolChoice is { } toolChoice)
        {
            payload["tool_choice"] = OpenAiResponsesTools.BuildToolChoice(toolChoice);
        }

        if (request.AllowParallelToolCalls is bool allowParallelToolCalls)
        {
            payload["parallel_tool_calls"] = allowParallelToolCalls;
        }

        if (request.StructuredOutput is { } structured
            && enforcement != LlmStructuredOutputEnforcement.LocalValidationOnly)
        {
            payload["text"] = BuildStructuredText(structured, enforcement!.Value);
        }

        if (request.ConversationKey is not null)
        {
            payload["prompt_cache_key"] = request.ConversationKey;
        }
        return payload;
    }

    /// <summary>Only a verified API-key profile requests a default summary; fallback effort levels are not evidence.</summary>
    protected virtual bool ReasonsByDefault(LlmConnection connection, string modelId) =>
        OpenAiModelProfiles.Get(modelId) is { Verified: true, IsReasoning: true };

    private static JsonElement CompleteOutput(JsonElement root, SortedDictionary<int, JsonElement> completedOutput)
    {
        JsonElement response = root.TryGetProperty("response", out var nested) ? nested : root;
        if (completedOutput.Count == 0 || (response.TryGetProperty("output", out var output)
                && output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0))
        {
            return root;
        }
        // Codex SSE may provide complete items only in output_item.done and an empty terminal
        // output array. Preserve those items in source order for tool completion and future replay.
        var node = JsonNode.Parse(root.GetRawText())!.AsObject();
        var target = node["response"] as JsonObject ?? node;
        target["output"] = new JsonArray([.. completedOutput.Values.Select(item => JsonNode.Parse(item.GetRawText()))]);
        return JsonSerializer.SerializeToElement(node);
    }

    protected virtual void ApplyRequestHeaders(
        HttpRequestMessage message, LlmConnection connection, LlmChatRequest request) =>
        ProviderHttp.ApplyHeaders(message, connection);

    private static JsonObject BuildStructuredText(
        LlmStructuredOutputRequest structured,
        LlmStructuredOutputEnforcement enforcement)
    {
        JsonObject format = enforcement switch
        {
            LlmStructuredOutputEnforcement.ProviderJsonSchema => new JsonObject
            {
                ["type"] = "json_schema",
                ["name"] = structured.Name,
                ["strict"] = structured.Strict,
                ["schema"] = JsonNode.Parse(structured.Schema.GetRawText())
            },
            LlmStructuredOutputEnforcement.ProviderJsonObject => new JsonObject
            {
                ["type"] = "json_object"
            },
            LlmStructuredOutputEnforcement.LocalValidationOnly =>
                throw new ArgumentException("Local validation does not add a provider response format.", nameof(enforcement)),
            _ => throw new ArgumentOutOfRangeException(nameof(enforcement))
        };

        if (!string.IsNullOrWhiteSpace(structured.Description)
            && enforcement == LlmStructuredOutputEnforcement.ProviderJsonSchema)
        {
            format["description"] = structured.Description;
        }

        return new JsonObject { ["format"] = format };
    }

    private static LlmChatResult BuildResult(
        JsonElement eventRoot,
        HttpResponseMessage response,
        string rawEvent,
        string streamedText,
        string streamedRefusal,
        LlmStructuredOutputRequest? structuredRequest,
        LlmStructuredOutputEnforcement? enforcement,
        LlmCredential? credential,
        LlmConnection connection,
        string modelId)
    {
        JsonElement responseRoot = eventRoot.TryGetProperty("response", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.Object
                ? nested
                : eventRoot;

        (string finalText, string finalRefusal) = ReadOutput(responseRoot);
        finalText = finalText.Length > 0 ? finalText : streamedText;
        finalRefusal = finalRefusal.Length > 0 ? finalRefusal : streamedRefusal;

        LlmToolCall[] toolCalls = OpenAiResponsesTools.ReadToolCalls(responseRoot);

        string? providerStatus = LlmJson.GetString(responseRoot, "status");
        LlmCompletionStatus status = finalRefusal.Length > 0
            ? LlmCompletionStatus.Refused
            : providerStatus switch
            {
                "incomplete" or "cancelled" => LlmCompletionStatus.Incomplete,
                "failed" => LlmCompletionStatus.Failed,
                _ when toolCalls.Length > 0 => LlmCompletionStatus.RequiresAction,
                _ => LlmCompletionStatus.Completed
            };

        string? incompleteReason = LlmJson.GetObject(responseRoot, "incomplete_details") is { } details
            ? LlmJson.GetString(details, "reason")
            : null;

        LlmStructuredOutputValidation? validation = null;
        if (structuredRequest is not null
            && enforcement is not null
            && status != LlmCompletionStatus.RequiresAction)
        {
            validation = finalRefusal.Length > 0
                ? new LlmStructuredOutputValidation(
                    enforcement.Value,
                    false,
                    false,
                    ["Structured output was not evaluated because the provider refused the request."])
                : StructuredOutput.Validate(finalText, structuredRequest, enforcement.Value);
        }

        LlmUsage? usage = ReadUsage(responseRoot);
        return new LlmChatResult
        {
            Text = finalText,
            Refusal = finalRefusal.Length == 0 ? null : finalRefusal,
            Status = status,
            FinishReason = status == LlmCompletionStatus.RequiresAction ? "tool_calls" : providerStatus,
            IncompleteReason = incompleteReason,
            Usage = usage,
            ProviderResponseId = LlmJson.GetString(responseRoot, "id"),
            ToolCalls = toolCalls,
            // The reported counts travel with the state, so later accounting can use them for this history.
            ContinuationState = status is LlmCompletionStatus.Failed or LlmCompletionStatus.Incomplete
                ? null
                : OpenAiResponsesTools.EncodeContinuationState(responseRoot, connection, modelId) is { } state
                    ? state with { InputTokens = usage?.InputTokens, OutputTokens = usage?.OutputTokens }
                    : null,
            StructuredOutput = validation,
            Diagnostics = new LlmProviderDiagnostics(
                (int)response.StatusCode,
                ProviderHttp.ReadRequestId(response),
                SecretRedactor.Sanitize(rawEvent, credential))
        };
    }

    private static (string Text, string Refusal) ReadOutput(JsonElement root)
    {
        var text = new StringBuilder();
        var refusal = new StringBuilder();
        if (!root.TryGetProperty("output", out JsonElement output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, string.Empty);
        }

        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement part in content.EnumerateArray())
            {
                switch (LlmJson.GetString(part, "type"))
                {
                    case "output_text":
                        text.Append(LlmJson.GetString(part, "text"));
                        break;
                    case "refusal":
                        refusal.Append(LlmJson.GetString(part, "refusal"));
                        break;
                }
            }
        }

        return (text.ToString(), refusal.ToString());
    }

    private static LlmUsage? ReadUsage(JsonElement root)
    {
        if (LlmJson.GetObject(root, "usage") is not { } usage)
        {
            return null;
        }

        return new LlmUsage(
            LlmJson.GetInt(usage, "input_tokens"),
            LlmJson.GetInt(usage, "output_tokens"),
            LlmJson.GetInt(usage, "total_tokens"))
        {
            CachedInputTokens = LlmJson.GetObject(usage, "input_tokens_details") is { } inputDetails
                ? LlmJson.GetInt(inputDetails, "cached_tokens") : null,
            ReasoningOutputTokens = LlmJson.GetObject(usage, "output_tokens_details") is { } outputDetails
                ? LlmJson.GetInt(outputDetails, "reasoning_tokens") : null
        };
    }

    private LlmProviderException StreamError(
        JsonElement root,
        HttpResponseMessage response,
        string rawEvent,
        LlmCredential? credential)
    {
        JsonElement error = LlmJson.GetObject(root, "error") ?? root;
        string providerMessage = SecretRedactor.Sanitize(
            LlmJson.GetString(error, "message") ?? "The provider returned a stream error.",
            credential);
        return new LlmProviderException(
            providerMessage,
            Transport,
            response.StatusCode,
            LlmJson.GetString(error, "code"),
            ProviderHttp.ReadRequestId(response),
            sanitizedResponseBody: SecretRedactor.Sanitize(rawEvent, credential));
    }
}
