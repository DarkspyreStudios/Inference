using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Shared Chat Completions projection, streaming and retained-output mapping.</summary>
internal abstract class ChatCompletionsLlmClientBase(HttpClient http) : LlmClientBase(http), ILlmConnectionPreparation, ILlmAccountUsageClient, ILlmContextEstimator
{
    private static readonly OpenAiCompatibleChatContinuationCodec Codec = new();

    protected override bool OffersNonStreamingRequests => true;
    protected override bool OffersUsageDeltas => true;

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        SupportsStreaming = true,
        SupportsReasoningText = true,
        SupportsStreamingUsage = true,
        SupportsModelListing = true,
        SupportsJsonSchema = false,
        SupportsJsonObject = false,
        SupportsPromptImages = false,
        SupportsPromptDocuments = false,
        RichContentSupported = RichContentRoutes.TextOnly,
        SupportsTemperature = true,
        SupportsTopP = true,
        SupportsSeed = true,
        SupportsPresencePenalty = true,
        SupportsFrequencyPenalty = true,
        MaxStopSequences = 4,
        SupportsToolUse = true,
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.None, LlmToolChoiceMode.Required, LlmToolChoiceMode.Specific }.ToFrozenSet(),
            StrictInputSchema = LlmStrictInputSchemaSupport.Unknown,
            SupportsParallelCalls = true,
            SupportsParallelCallControl = true,
            SupportsArgumentStreaming = true
        }
    };

    public override LlmProviderCapabilities GetCapabilities(
        LlmConnection connection,
        string? modelId = null)
    {
        if (connection.ChatCompletions?.OutputLimitField is { } field && !Enum.IsDefined(field))
            throw new ArgumentOutOfRangeException(nameof(connection), "The Chat Completions output-limit field is invalid.");
        bool openRouter = IsOpenRouter(connection);
        if (!openRouter && (connection.CapabilityOverrides?.SupportsAccountUsage == true
            || modelId is not null && connection.ModelCapabilityOverrides.TryGetValue(modelId, out var overrides)
            && overrides.SupportsAccountUsage == true))
        { throw new NotSupportedException("Account usage is implemented only for OpenRouter endpoints."); }
        LlmProviderCapabilities capabilities = base.GetCapabilities(connection, modelId);
        bool enabled = openRouter;
        if (connection.CapabilityOverrides?.SupportsAccountUsage is { } routeEnabled) { enabled = routeEnabled; }
        if (modelId is not null && connection.ModelCapabilityOverrides.TryGetValue(modelId, out var modelOverrides)
            && modelOverrides.SupportsAccountUsage is { } modelEnabled) { enabled = modelEnabled; }
        return capabilities with
        {
            SupportsAccountUsage = enabled,
            PromptCache = openRouter ? OpenRouterPromptCache.Capabilities(modelId) : capabilities.PromptCache
        };
    }

    protected virtual bool IsOpenRouter(LlmConnection connection) => Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out Uri? endpoint)
        && endpoint.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase);

    public async Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        if (!GetCapabilities(connection).SupportsAccountUsage) { throw new NotSupportedException("This connection does not expose account usage."); }
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        LlmAccountUsageSnapshot result;
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.BuildUri(connection, "key"));
            ProviderHttp.ApplyHeaders(request, connection);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            await ProviderHttp.ThrowIfUnsuccessfulAsync(response, Transport, LlmCredential.Parse(connection.Credential),
                "OpenRouter key usage", cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(await SdkHttpConsumption.ReadAsStreamAsync(response.Content, cancellationToken), cancellationToken: cancellationToken);
            result = OpenRouterAccountUsageParser.Parse(document.RootElement, DateTimeOffset.UtcNow, LlmCredential.Parse(connection.Credential));
        }
        return consumption.Publish(result);
    }

    public Task<LlmConnection> PrepareAsync(LlmConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = GetCapabilities(connection);
        return Task.FromResult(Prepare(connection));
    }

    private static LlmConnection Prepare(LlmConnection connection)
    {
        string scope = ProviderContinuationStateCodec.Scope(connection with { ContinuationCodec = null, ResolvedContinuationScope = null });
        if (connection.ResolvedContinuationScope is { } pinned && pinned != scope)
        { throw new InvalidOperationException("The chat connection identity changed before dispatch."); }
        return connection with { ContinuationCodec = Codec, ResolvedContinuationScope = scope };
    }

    public string? EstimateDiagnostic =>
        "Estimated from the Chat Completions plaintext projection with a general-purpose tokenizer; media and opaque provider state remain unknown.";

    public ContextEstimate? EstimateInputTokens(LlmConnection connection, LlmChatRequest request)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        ValidateModelSampling(connection, request);
        connection = Prepare(connection);
        request = ContinuationStateProjection.Project(request, connection).Request;
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is null
            ? null : StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);
        JsonObject payload = BuildPayload(connection, request, enforcement, capabilities);
        if (request.Resources is not null || payload["messages"] is not JsonArray messages
            || messages.Any(message => message is not JsonObject item || !IsPlaintextMessage(item))) return null;
        var input = new JsonObject
        {
            ["messages"] = messages.DeepClone(),
            ["tools"] = payload["tools"]?.DeepClone(),
            ["tool_choice"] = payload["tool_choice"]?.DeepClone(),
            ["response_format"] = payload["response_format"]?.DeepClone()
        };
        return new ContextEstimate(LocalTokenCount.Count(input.ToJsonString()));
    }

    private static bool IsPlaintextMessage(JsonObject message)
    {
        bool text = message["content"] is null
            || message["content"] is JsonValue value && value.TryGetValue<string>(out _)
            || message["content"] is JsonArray parts && parts.All(part => part is JsonObject item
                && (string?)item["type"] == "text" && item["text"] is JsonValue content && content.TryGetValue<string>(out _));
        return text && (message["reasoning_details"] is null || message["reasoning_details"] is JsonArray details
            && details.All(detail => detail is JsonObject item && (string?)item["type"] is "reasoning.text" or "reasoning.summary"
                && item["signature"] is null));
    }

    public override async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        ValidateModelSampling(connection, request);
        connection = Prepare(connection);
        (request, int omitted) = ContinuationStateProjection.Project(request, connection);
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is null
            ? null
            : StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);

        JsonObject payload = BuildPayload(connection, request, enforcement, capabilities);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            BuildRequestUri(connection, request))
        {
            Content = RequestJson.Content(payload)
        };
        ApplyRequestHeaders(httpRequest, connection, request);

        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        using HttpResponseMessage response = await Http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (ObserveAccountUsage(response) is { } observed)
        { yield return consumption.Publish<LlmStreamEvent>(new AccountUsageObservedEvent(observed)); }
        await ProviderHttp.ThrowIfUnsuccessfulAsync(
            response,
            Transport,
            credential,
            "OpenAI-compatible chat request",
            cancellationToken);

        if (!capabilities.SupportsStreaming)
        {
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
            foreach (LlmStreamEvent completeEvent in ReadCompleteResponse(body, response, request, enforcement, credential, connection, omitted, capabilities.SupportsReasoningText))
            {
                yield return consumption.Publish<LlmStreamEvent>(completeEvent);
            }

            yield break;
        }

        await using Stream stream = await SdkHttpConsumption.ReadAsStreamAsync(response.Content, cancellationToken);
        var text = new StringBuilder();
        var refusal = new StringBuilder();
        string? finishReason = null;
        string? responseId = null;
        string? observedModel = null;
        LlmUsage? usage = null;
        string? lastEvent = null;
        bool sawDone = false;
        var toolCalls = new Dictionary<int, ToolCallBuilder>();
        var usageDeltas = new UsageDeltas(capabilities.SupportsUsageDeltas);
        var reasoning = new ChatReasoning();

        await foreach (string data in Sse.ReadDataPayloadsAsync(stream, cancellationToken))
        {
            if (data == "[DONE]")
            {
                sawDone = true;
                break;
            }

            lastEvent = data;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(data);
            }
            catch (JsonException exception)
            {
                throw ProtocolException(
                    "OpenAI-compatible chat returned a malformed stream event.",
                    response,
                    data,
                    credential,
                    exception);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                responseId ??= LlmJson.GetString(root, "id");
                observedModel = ObservedModel.Read(root, "model", Transport, connection, credential,
                    response.RequestMessage, observedModel);
                if (LlmJson.GetObject(root, "error") is { } error)
                {
                    throw StreamError(error, response, data, credential);
                }

                usage = ReadUsage(root) ?? usage;
                if (usageDeltas.Next(usage) is { } observation)
                {
                    yield return consumption.Publish<LlmStreamEvent>(observation);
                }

                if (!root.TryGetProperty("choices", out JsonElement choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    continue;
                }

                JsonElement choice = choices[0];
                finishReason = LlmJson.GetString(choice, "finish_reason") ?? finishReason;
                if (!choice.TryGetProperty("delta", out JsonElement delta)
                    || delta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string contentDelta = LlmJson.GetString(delta, "content") ?? string.Empty;
                if (reasoning.Observe(delta) is { } reasoningDelta && capabilities.SupportsReasoningText) { yield return consumption.Publish<LlmStreamEvent>(reasoningDelta); }
                if (contentDelta.Length > 0)
                {
                    text.Append(contentDelta);
                    yield return consumption.Publish<LlmStreamEvent>(new LlmTextDeltaEvent(contentDelta));
                }

                string refusalDelta = LlmJson.GetString(delta, "refusal") ?? string.Empty;
                if (refusalDelta.Length > 0)
                {
                    refusal.Append(refusalDelta);
                    yield return consumption.Publish<LlmStreamEvent>(new LlmRefusalDeltaEvent(refusalDelta));
                }

                if (delta.TryGetProperty("tool_calls", out JsonElement toolCallDeltas)
                    && toolCallDeltas.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement toolCallDelta in toolCallDeltas.EnumerateArray())
                    {
                        int index = LlmJson.GetInt(toolCallDelta, "index") ?? toolCalls.Count;
                        if (!toolCalls.TryGetValue(index, out ToolCallBuilder? builder))
                        {
                            builder = new ToolCallBuilder();
                            toolCalls.Add(index, builder);
                        }

                        if (LlmJson.GetString(toolCallDelta, "id") is { Length: > 0 } incomingId)
                        {
                            if (!builder.SyntheticId && builder.Id is { } previous && previous != incomingId)
                                throw ProtocolException("OpenAI-compatible chat changed a tool-call id mid-stream.", response, data, credential);
                            if (!builder.SyntheticId) builder.Id = incomingId;
                        }
                        string argumentsDelta = string.Empty;
                        if (LlmJson.GetObject(toolCallDelta, "function") is { } function)
                        {
                            if (LlmJson.GetString(function, "name") is { Length: > 0 } incomingName)
                                builder.Name ??= incomingName;
                            argumentsDelta = LlmJson.GetString(function, "arguments")
                                ?? string.Empty;
                        }

                        if (!builder.Started && builder.Name is { } name)
                        {
                            builder.EnsureId();
                            builder.Started = true;
                            yield return consumption.Publish<LlmStreamEvent>(new LlmToolCallStartedEvent(builder.Id!, name, index));
                            if (builder.Pending.Length > 0)
                            {
                                yield return consumption.Publish<LlmStreamEvent>(new LlmToolCallArgumentsDeltaEvent(builder.Id!, builder.Pending.ToString()));
                                builder.Pending.Clear();
                            }
                        }

                        if (argumentsDelta.Length > 0)
                        {
                            builder.Arguments.Append(argumentsDelta);
                            if (builder.Started)
                            {
                                yield return consumption.Publish<LlmStreamEvent>(new LlmToolCallArgumentsDeltaEvent(
                                    builder.Id!,
                                    argumentsDelta));
                            }
                            else builder.Pending.Append(argumentsDelta);
                        }
                    }
                }
            }
        }

        if (!sawDone && finishReason is null)
        {
            throw ProtocolException(
                "OpenAI-compatible chat ended without a completion marker.",
                response,
                lastEvent ?? string.Empty,
                credential);
        }

        LlmToolCall[] completedToolCalls = toolCalls
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Build())
            .ToArray();
        foreach (LlmToolCall toolCall in completedToolCalls)
        {
            yield return consumption.Publish<LlmStreamEvent>(new LlmToolCallCompletedEvent(toolCall));
        }

        yield return consumption.Publish<LlmStreamEvent>(new LlmChatCompletionEvent(BuildResult(
            request,
            enforcement,
            text.ToString(),
            refusal.ToString(),
            finishReason,
            responseId,
            observedModel,
            usage,
            completedToolCalls,
            response,
            lastEvent,
            credential,
            connection,
            reasoning,
            omitted)));
    }

    /// <summary>Maps a response that arrived as one chat.completion object.</summary>
    private List<LlmStreamEvent> ReadCompleteResponse(
        string body,
        HttpResponseMessage response,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        LlmCredential? credential,
        LlmConnection connection,
        int omitted,
        bool reasoningText)
    {
        ChatCompletionResponse decoded = DecodeCompleteResponse(body, response, connection, request, enforcement, omitted);
        return [.. CompleteResponseEvents.From(decoded.Result, reasoningText ? decoded.Reasoning : [])];
    }

    internal sealed record ChatCompletionOutputFile(string Id, string Name);

    internal sealed record ChatCompletionResponse(
        LlmChatResult Result,
        IReadOnlyList<LlmReasoningText> Reasoning,
        IReadOnlyList<ChatCompletionOutputFile> OutputFiles);

    internal ChatCompletionResponse DecodeCompleteResponse(
        string body,
        HttpResponseMessage response,
        LlmConnection connection,
        LlmChatRequest? request = null,
        LlmStructuredOutputEnforcement? enforcement = null,
        int omitted = 0)
    {
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw ProtocolException(
                "OpenAI-compatible chat returned a malformed response.",
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
                throw ProtocolException("OpenAI-compatible chat returned a malformed response.", response, body, credential);
            }

            if (LlmJson.GetObject(root, "error") is { } error)
            {
                throw StreamError(error, response, body, credential);
            }

            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || LlmJson.GetObject(choices[0], "message") is not { } message)
            {
                throw ProtocolException("OpenAI-compatible chat returned no message.", response, body, credential);
            }

            var toolCalls = new List<LlmToolCall>();
            var reasoning = new ChatReasoning();
            LlmReasoningDeltaEvent? observation = reasoning.Observe(message);
            if (message.TryGetProperty("tool_calls", out JsonElement calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement call in calls.EnumerateArray())
                {
                    var builder = new ToolCallBuilder { Id = LlmJson.GetString(call, "id") };
                    if (LlmJson.GetObject(call, "function") is { } function)
                    {
                        builder.Name = LlmJson.GetString(function, "name");
                        builder.Arguments.Append(LlmJson.GetString(function, "arguments"));
                    }

                    toolCalls.Add(builder.Build());
                }
            }

            LlmChatResult result = BuildResult(
                request,
                enforcement,
                LlmJson.GetString(message, "content") ?? string.Empty,
                LlmJson.GetString(message, "refusal") ?? string.Empty,
                LlmJson.GetString(choices[0], "finish_reason"),
                LlmJson.GetString(root, "id"),
                ObservedModel.Read(root, "model", Transport, connection, credential, response.RequestMessage),
                ReadUsage(root),
                [.. toolCalls],
                response,
                body,
                credential,
                connection,
                reasoning,
                omitted);
            var outputFiles = new List<ChatCompletionOutputFile>();
            if (Transport == LlmTransport.XaiResponses)
            {
                var citations = new List<LlmCitation>();
                if (root.TryGetProperty("citations", out JsonElement sources) && sources.ValueKind != JsonValueKind.Null)
                {
                    if (sources.ValueKind != JsonValueKind.Array)
                        throw ProtocolException("xAI chat returned invalid citations.", response, body, credential);
                    foreach (JsonElement source in sources.EnumerateArray())
                    {
                        if (source.ValueKind != JsonValueKind.String)
                            throw ProtocolException("xAI chat returned an invalid citation.", response, body, credential);
                        citations.Add(new LlmCitation(source.GetString()!));
                    }
                }
                result = result with { Citations = citations.ToArray() };
                if (root.TryGetProperty("output_files", out JsonElement files) && files.ValueKind != JsonValueKind.Null)
                {
                    if (files.ValueKind != JsonValueKind.Array)
                        throw ProtocolException("xAI chat returned invalid generated files.", response, body, credential);
                    foreach (JsonElement file in files.EnumerateArray())
                    {
                        if (file.ValueKind != JsonValueKind.Object
                            || !file.TryGetProperty("file_id", out JsonElement id) || id.ValueKind != JsonValueKind.String
                            || !file.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String)
                            throw ProtocolException("xAI chat returned an invalid generated file.", response, body, credential);
                        outputFiles.Add(new(id.GetString()!, name.GetString()!));
                    }
                }
            }
            return new(result, observation is null ? [] : [new(observation.Delta, observation.IsSummary)], outputFiles.ToArray());
        }
    }

    private static LlmChatResult BuildResult(
        LlmChatRequest? request,
        LlmStructuredOutputEnforcement? enforcement,
        string finalText,
        string finalRefusal,
        string? finishReason,
        string? responseId,
        string? observedModel,
        LlmUsage? usage,
        LlmToolCall[] completedToolCalls,
        HttpResponseMessage response,
        string? rawFinal,
        LlmCredential? credential,
        LlmConnection connection,
        ChatReasoning reasoning,
        int omitted)
    {
        LlmCompletionStatus status = finalRefusal.Length > 0 || finishReason == "content_filter"
            ? LlmCompletionStatus.Refused
            : finishReason == "length"
                ? LlmCompletionStatus.Incomplete
                : finishReason == "tool_calls" || finishReason == "stop" && completedToolCalls.Length > 0
                    ? LlmCompletionStatus.RequiresAction
                    : finishReason == "stop"
                        ? LlmCompletionStatus.Completed
                        : LlmCompletionStatus.Unknown;

        LlmStructuredOutputValidation? validation = null;
        if (request?.StructuredOutput is { } structured
            && enforcement is not null
            && status != LlmCompletionStatus.RequiresAction)
        {
            validation = status == LlmCompletionStatus.Refused
                ? new LlmStructuredOutputValidation(
                    enforcement.Value,
                    false,
                    false,
                    ["Structured output was not evaluated because the provider refused the request."])
                : StructuredOutput.Validate(finalText, structured, enforcement.Value);
        }

        return new LlmChatResult
        {
            Text = finalText,
            ModelId = observedModel,
            Refusal = finalRefusal.Length == 0 ? null : finalRefusal,
            Status = status,
            FinishReason = finishReason,
            IncompleteReason = status == LlmCompletionStatus.Incomplete ? finishReason : null,
            Usage = usage,
            ProviderResponseId = responseId,
            ToolCalls = completedToolCalls,
            ContinuationState = EncodeReasoning(connection, request?.ModelId ?? observedModel, finalText, completedToolCalls, status, reasoning),
            OmittedContinuationStateCount = omitted,
            StructuredOutput = validation,
            Diagnostics = new LlmProviderDiagnostics(
                (int)response.StatusCode,
                ProviderHttp.ReadRequestId(response),
                rawFinal is null ? null : SecretRedactor.Sanitize(rawFinal, credential))
        };
    }

    private static LlmContinuationState? EncodeReasoning(LlmConnection connection, string? modelId, string text,
        IReadOnlyList<LlmToolCall> toolCalls, LlmCompletionStatus status, ChatReasoning reasoning)
    {
        string? content = reasoning.ReasoningContent;
        JsonArray? details = reasoning.ReasoningDetails;
        if (modelId is null || status is not (LlmCompletionStatus.Completed or LlmCompletionStatus.RequiresAction) || content is null && details is null)
        { return null; }
        var item = new JsonObject
        {
            ["type"] = "chat_message",
            ["text"] = text,
            ["tool_calls"] = new JsonArray([.. toolCalls.Select(call => (JsonNode)new JsonObject
            { ["id"] = call.Id, ["name"] = call.Name, ["arguments"] = call.ArgumentsJson })])
        };
        if (content is not null) { item["reasoning_content"] = content; }
        if (details is not null) { item["reasoning_details"] = details; }
        return Codec.Encode(connection, modelId, new JsonArray(item));
    }

    protected virtual JsonObject BuildPayload(
        LlmConnection connection,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        LlmProviderCapabilities capabilities,
        LlmResourcePreparation? resources = null)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            messages.Add(item: new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.Instructions
            });
        }

        foreach (LlmChatMessage message in request.Messages)
        {
            JsonObject? retained = message.ContinuationState is null ? null
                : (JsonObject)ProviderContinuationStateCodec.DecodeMessage(message, connection, request.ModelId)[0]!;
            if (message.ToolResults.Count > 0)
            {
                foreach (LlmToolResult result in ClientTools.Functions(message.ToolResults))
                {
                    ProjectedToolOutput output = ToolOutputProjector.Project(
                        result.Output, capabilities.RichContentSupported, capabilities.RichContentSupported,
                        resources is null ? null : resources.Project);
                    messages.Add(item: new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = result.ToolCallId,
                        ["content"] = BuildToolContent(output)
                    });
                }

                if (message.Content.Length == 0)
                {
                    continue;
                }
            }

            var mappedMessage = new JsonObject
            {
                ["role"] = message.Role,
                ["content"] = message.Attachments.Count > 0 ? BuildContentParts(message)
                    : message.Content.Length == 0 && message.ToolCalls.Count > 0
                    ? null
                    : message.Content
            };
            if (message.ToolCalls.Count > 0)
            {
                var mappedCalls = new JsonArray();
                foreach (LlmToolCall call in ClientTools.Functions(message.ToolCalls))
                {
                    mappedCalls.Add(item: new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.ArgumentsJson
                        }
                    });
                }

                mappedMessage["tool_calls"] = mappedCalls;
            }

            if (retained is not null && request.Tools.Count > 0)
            {
                if (retained["reasoning_details"] is { } details) { mappedMessage["reasoning_details"] = details.DeepClone(); }
                else if (retained["reasoning_content"] is { } content) { mappedMessage["reasoning_content"] = content.DeepClone(); }
            }

            messages.Add(item: mappedMessage);
        }

        var payload = new JsonObject
        {
            ["model"] = request.ModelId,
            ["messages"] = messages,
            ["stream"] = capabilities.SupportsStreaming
        };

        // OpenAI accepts stream_options only on streaming requests; a non-streaming body always carries usage.
        if (capabilities.SupportsStreaming && capabilities.SupportsStreamingUsage)
        {
            var options = new JsonObject { ["include_usage"] = true };
            if (capabilities.SupportsUsageDeltas)
            {
                options["continuous_usage_stats"] = true;
            }
            payload["stream_options"] = options;
        }

        if (request.Temperature is double temperature)
        {
            payload["temperature"] = temperature;
        }
        if (request.TopP is { } topP) { payload["top_p"] = topP; }
        if (request.TopK is { } topK) { payload["top_k"] = topK; }
        if (request.Seed is { } seed) { payload["seed"] = seed; }
        if (request.PresencePenalty is { } presence) { payload["presence_penalty"] = presence; }
        if (request.FrequencyPenalty is { } frequency) { payload["frequency_penalty"] = frequency; }
        if (request.StopSequences.Count > 0) { payload["stop"] = new JsonArray([.. request.StopSequences.Select(stop => JsonValue.Create(stop))]); }
        if (request.PresencePenalty is < -2 or > 2 || request.FrequencyPenalty is < -2 or > 2)
        { throw new ArgumentOutOfRangeException(nameof(request), "Chat Completions penalties must be between -2 and 2."); }

        if (request.MaxOutputTokens is int maxOutputTokens)
        {
            LlmChatCompletionsOutputLimitField field = OutputLimitField(connection);
            payload[field == LlmChatCompletionsOutputLimitField.MaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = maxOutputTokens;
        }

        if (request.ReasoningLevel is { } level)
        {
            payload["reasoning_effort"] = ReasoningWord(level);
        }

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (LlmToolDefinition tool in ClientTools.Functions(request.Tools))
            {
                var function = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["parameters"] = JsonNode.Parse(tool.InputSchema.GetRawText())
                };
                // Sent only for a strict tool: compatible servers can reject the field even when false.
                if (tool.Strict)
                {
                    function["strict"] = true;
                }
                if (!string.IsNullOrWhiteSpace(tool.Description))
                {
                    function["description"] = tool.Description;
                }

                tools.Add(item: new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = function
                });
            }

            payload["tools"] = tools;
        }

        if (request.ToolChoice is { } toolChoice)
        {
            payload["tool_choice"] = toolChoice.Mode switch
            {
                LlmToolChoiceMode.Auto => "auto",
                LlmToolChoiceMode.None => "none",
                LlmToolChoiceMode.Required => "required",
                LlmToolChoiceMode.Specific => new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = toolChoice.Name }
                },
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };
        }

        if (request.AllowParallelToolCalls is bool allowParallelToolCalls)
        {
            payload["parallel_tool_calls"] = allowParallelToolCalls;
        }

        if (request.StructuredOutput is { } structured)
        {
            switch (enforcement)
            {
                case LlmStructuredOutputEnforcement.ProviderJsonSchema:
                    payload["response_format"] = new JsonObject
                    {
                        ["type"] = "json_schema",
                        ["json_schema"] = BuildJsonSchema(structured)
                    };
                    break;
                case LlmStructuredOutputEnforcement.ProviderJsonObject:
                    payload["response_format"] = new JsonObject { ["type"] = "json_object" };
                    break;
                case LlmStructuredOutputEnforcement.LocalValidationOnly:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(enforcement));
            }
        }

        if (request.ConversationKey is { Length: > 0 } key
            && UsePromptCacheKey(connection))
            payload["prompt_cache_key"] = key;

        if (IsOpenRouter(connection)) OpenRouterPromptCache.Apply(payload, connection, request);
        RequestJson.ApplyProviderOptions(payload, connection, request);
        return payload;
    }

    protected virtual Uri BuildRequestUri(LlmConnection connection, LlmChatRequest request) =>
        ProviderHttp.BuildUri(connection, "chat/completions");

    protected virtual void ApplyRequestHeaders(HttpRequestMessage message, LlmConnection connection, LlmChatRequest request) =>
        ProviderHttp.ApplyHeaders(message, connection);

    protected virtual void ValidateModelSampling(LlmConnection connection, LlmChatRequest request) { }

    protected virtual string ReasoningWord(LlmReasoningLevel level) => LlmReasoningEffort.Word(level);

    protected virtual LlmAccountUsageSnapshot? ObserveAccountUsage(HttpResponseMessage response) =>
        OpenAiRateLimitHeaders.Parse(response.Headers, DateTimeOffset.UtcNow);

    protected virtual LlmChatCompletionsOutputLimitField OutputLimitField(LlmConnection connection) =>
        connection.ChatCompletions?.OutputLimitField
        ?? (IsOpenAiEndpoint(connection) ? LlmChatCompletionsOutputLimitField.MaxCompletionTokens : LlmChatCompletionsOutputLimitField.MaxTokens);

    protected virtual bool UsePromptCacheKey(LlmConnection connection) =>
        connection.ChatCompletions?.UsePromptCacheKey ?? IsOpenAiEndpoint(connection);

    private static bool IsOpenAiEndpoint(LlmConnection connection) =>
        Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out Uri? endpoint)
        && endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);

    internal void ValidatePreparedRequest(LlmConnection connection, LlmChatRequest request,
        LlmProviderCapabilities capabilities)
    {
        ValidateRequest(connection, request, capabilities);
        ValidateModelSampling(connection, request);
    }

    internal JsonObject BuildPreparedPayload(LlmConnection connection, LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement, LlmProviderCapabilities capabilities,
        LlmResourcePreparation? resources = null) =>
        BuildPayload(connection, request, enforcement, capabilities, resources);

    private static JsonNode BuildToolContent(ProjectedToolOutput output)
    {
        if (!output.Parts.Any(part => part is SentNativeFile)) return JsonValue.Create(output.Text)!;
        var parts = new JsonArray();
        foreach (SentToolPart part in output.Parts)
        {
            switch (part)
            {
                case SentText text:
                    parts.Add(item: new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;
                case SentNativeFile file:
                    parts.Add(item: BuildNativeFile(file.FileId));
                    break;
            }
        }
        return parts;
    }

    private static JsonObject BuildNativeFile(string fileId) => new()
    {
        ["type"] = "file",
        ["file"] = new JsonObject { ["file_id"] = XaiFileRequestData.Identity(fileId) }
    };

    private JsonArray BuildContentParts(LlmChatMessage message)
    {
        var parts = new JsonArray();
        if (message.Content.Length > 0) parts.Add(item: new JsonObject { ["type"] = "text", ["text"] = message.Content });
        foreach (LlmMessageAttachment source in message.Attachments)
        {
            if (Transport == LlmTransport.XaiResponses && source is LlmFileReference file)
            {
                parts.Add(item: BuildNativeFile(file.File.FileId));
                continue;
            }
            LlmAttachment attachment = LlmAttachments.RequireBinary(source);
            parts.Add(item: attachment.Kind switch
            {
                LlmBinaryKind.Image => new JsonObject
                { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = LlmAttachments.ToDataUrl(attachment) } },
                LlmBinaryKind.Document when LlmAttachments.ResolveMediaType(attachment) == "application/pdf" => new JsonObject
                {
                    ["type"] = "file",
                    ["file"] = new JsonObject
                    { ["filename"] = attachment.FileName, ["file_data"] = LlmAttachments.ToDataUrl(attachment) }
                },
                LlmBinaryKind.Document when LlmAttachments.ResolveMediaType(attachment).StartsWith("text/", StringComparison.Ordinal) =>
                    new JsonObject { ["type"] = "text", ["text"] = LlmAttachments.ReadText(attachment) },
                LlmBinaryKind.Document => throw new NotSupportedException("OpenAI-compatible chat carries PDF and UTF-8 text documents only."),
                _ => throw new ArgumentOutOfRangeException(nameof(message))
            });
        }
        return parts;
    }

    private static JsonObject BuildJsonSchema(LlmStructuredOutputRequest structured)
    {
        var value = new JsonObject
        {
            ["name"] = structured.Name,
            ["strict"] = structured.Strict,
            ["schema"] = JsonNode.Parse(structured.Schema.GetRawText())
        };

        if (!string.IsNullOrWhiteSpace(structured.Description))
        {
            value["description"] = structured.Description;
        }

        return value;
    }

    protected virtual LlmUsage? ReadUsage(JsonElement root)
    {
        if (LlmJson.GetObject(root, "usage") is not { } usage)
        {
            return null;
        }

        return new LlmUsage(
            LlmJson.GetInt(usage, "prompt_tokens"),
            LlmJson.GetInt(usage, "completion_tokens"),
            LlmJson.GetInt(usage, "total_tokens"))
        {
            CachedInputTokens = LlmJson.GetObject(usage, "prompt_tokens_details") is { } inputDetails
                ? LlmJson.GetInt(inputDetails, "cached_tokens") : null,
            ReasoningOutputTokens = LlmJson.GetObject(usage, "completion_tokens_details") is { } outputDetails
                ? LlmJson.GetInt(outputDetails, "reasoning_tokens") : null
        };
    }

    private LlmProviderException StreamError(
        JsonElement error,
        HttpResponseMessage response,
        string rawEvent,
        LlmCredential? credential) => new(
            SecretRedactor.Sanitize(
                LlmJson.GetString(error, "message") ?? "The provider returned a stream error.",
                credential),
            Transport,
            response.StatusCode,
            LlmJson.GetString(error, "code"),
            ProviderHttp.ReadRequestId(response),
            sanitizedResponseBody: SecretRedactor.Sanitize(rawEvent, credential),
            isContextOverflow: LlmContextOverflow.Chat(LlmJson.GetString(error, "code"), LlmJson.GetString(error, "message")));

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }

        public bool SyntheticId { get; private set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();

        public StringBuilder Pending { get; } = new();

        public bool Started { get; set; }

        public void EnsureId()
        {
            if (!string.IsNullOrWhiteSpace(Id)) return;
            Id = "call_" + Guid.NewGuid().ToString("N");
            SyntheticId = true;
        }

        public LlmToolCall Build()
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                throw new InvalidOperationException(
                    "The provider returned an incomplete tool-call stream.");
            }

            EnsureId();
            return new LlmToolCall(
                Id!,
                Name,
                Arguments.Length == 0 ? "{}" : Arguments.ToString());
        }
    }
}
