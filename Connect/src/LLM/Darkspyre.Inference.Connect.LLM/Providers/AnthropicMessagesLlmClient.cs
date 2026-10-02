using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Direct Anthropic Messages API adapter for API-key connections.</summary>
/// <remarks>
/// It reads the first-party Models API catalog for per-model reasoning levels. Vertex Claude, which derives from it,
/// has no Models API; the service never attaches a catalog to a Vertex connection.
/// </remarks>
internal class AnthropicMessagesLlmClient : LlmClientBase, ILlmCatalogCapabilities, ILlmProviderTokenCounter
{
    private const string AnthropicVersion = "2023-06-01";
    private const int DefaultMaxOutputTokens = 8192;
    private const int ModelPageSize = 1000;
    private static readonly string[] CountExcludedFields =
        ["max_tokens", "stream", "temperature", "top_p", "top_k", "stop_sequences", "service_tier", "anthropic_version"];

    public AnthropicMessagesLlmClient(HttpClient http) : base(http)
    {
    }

    public override LlmTransport Transport => LlmTransport.AnthropicMessages;

    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.AnthropicApiKey;

    protected override bool OffersNonStreamingRequests => true;

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        OutputLimitEnforcement = Common.LLM.LlmOutputLimitEnforcement.ProviderEnforced,
        SupportsStreaming = true,
        SupportsReasoningText = true,
        SupportsStreamingUsage = true,
        SupportsUsageDeltas = true,
        SupportsModelListing = true,
        SupportsAccountUsage = false,
        SupportsJsonSchema = false,
        SupportsJsonObject = false,
        SupportsTemperature = false,
        SupportsToolUse = true,
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.None, LlmToolChoiceMode.Required, LlmToolChoiceMode.Specific }.ToFrozenSet(),
            StrictInputSchema = LlmStrictInputSchemaSupport.Unknown,
            SupportsParallelCalls = true,
            SupportsParallelCallControl = true,
            SupportsArgumentStreaming = true
        },
        SupportsPromptImages = true,
        SupportsPromptDocuments = true,
        RichContentSupported = RichContentRoutes.Anthropic
    };

    public override LlmProviderCapabilities GetCapabilities(
        LlmConnection connection,
        string? modelId = null)
    {
        LlmProviderCapabilities capabilities = base.GetCapabilities(connection, modelId);
        AnthropicModelProfile profile = AnthropicModelProfiles.Resolve(modelId, ILlmCatalogCapabilities.EntryFor(connection, modelId));
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if (credential?.OAuth is not null || credential?.Aws is not null)
        {
            throw new NotSupportedException(
                "Anthropic Messages API connections accept only an API key or x-api-key secret header.");
        }
        return capabilities with
        {
            SupportsJsonSchema = profile.SupportsStructuredOutputs != false
                && (LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.SupportsJsonSchema)
                    ? capabilities.SupportsJsonSchema : profile.SupportsStructuredOutputs == true),
            SupportsJsonObject = false,
            Tools = capabilities.Tools with
            {
                StrictInputSchema = profile.SupportsStructuredOutputs switch
                {
                    true => LlmStrictInputSchemaSupport.Supported,
                    false => LlmStrictInputSchemaSupport.Unsupported,
                    null => LlmStrictInputSchemaSupport.Unknown
                }
            },
            SupportsAccountUsage = false,
            SupportsAgentSessions = false,
            SupportsAgentToolUse = false,
            SupportsTemperature = profile.AllowsSampling
                && (!LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.SupportsTemperature) || capabilities.SupportsTemperature),
            SupportsTopP = profile.AllowsSampling
                && (!LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.SupportsTopP) || capabilities.SupportsTopP),
            SupportsTopK = profile.AllowsSampling
                && (!LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.SupportsTopK) || capabilities.SupportsTopK),
            SupportsSeed = false,
            SupportsPresencePenalty = false,
            SupportsFrequencyPenalty = false,
            MaxStopSequences = LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.MaxStopSequences)
                ? capabilities.MaxStopSequences : int.MaxValue,
            ReasoningLevels = LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.ReasoningLevels)
                ? capabilities.ReasoningLevels
                : profile.ReasoningLevels
        };
    }

    private (LlmProviderCapabilities Capabilities, LlmStructuredOutputEnforcement? Enforcement) ValidateMessagesRequest(
        LlmConnection connection, LlmChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities, AnthropicTools.Resolve(connection, request).NativeNames);
        AnthropicModelProfiles.ValidateSampling(request, AnthropicModelProfiles.Resolve(
            ReasoningModelId(request.ModelId), ILlmCatalogCapabilities.EntryFor(connection, request.ModelId)));
        LlmStructuredOutputEnforcement? enforcement = AnthropicSchemas.Enforcement(request, capabilities);
        return (capabilities, enforcement);
    }

    public async Task<LlmProviderInputTokenCount> CountInputTokensAsync(LlmConnection connection, LlmChatRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTokenCountOptions(connection, request);
        _ = ValidateMessagesRequest(connection, request);
        JsonObject payload = BuildPayload(connection, request, output => ProjectToolOutput(connection, request.ModelId, output), false);
        // Count APIs exclude generation-only controls; every prompt-bearing field remains unchanged.
        foreach (string name in CountExcludedFields)
            payload.Remove(name);
        if (payload.Any(field => !IsTokenCountField(field.Key)))
            throw new NotSupportedException("The Anthropic count API cannot represent one of the configured provider fields.");
        using HttpRequestMessage message = CreateTokenCountRequest(connection, request, payload);
        return await ProviderTokenCounting.SendAsync(Http, message, Transport, request.ModelId, true, cancellationToken).ConfigureAwait(false);
    }

    internal static void ValidateTokenCountOptions(LlmConnection connection, LlmChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (connection.ProviderOptions.Keys.Concat(request.ProviderOptions.Keys).Any(name => name != AnthropicTools.OptionName
            && !IsTokenCountField(name) && !CountExcludedFields.Contains(name, StringComparer.Ordinal)))
            throw new NotSupportedException("The Anthropic count API cannot represent one of the configured provider fields.");
    }

    private static bool IsTokenCountField(string name) => name is
        "model" or "messages" or "system" or "tools" or "tool_choice" or "thinking" or "output_config" or "cache_control";

    public override async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (capabilities, enforcement) = ValidateMessagesRequest(connection, request);
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        bool stream = capabilities.SupportsStreaming;
        JsonObject payload = BuildPayload(connection, request, output => ProjectToolOutput(connection, request.ModelId, output), stream);
        using HttpRequestMessage httpRequest = CreateHttpRequest(connection, request, payload, stream);

        using HttpResponseMessage response = await Http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (ObserveAccountUsage(response) is { } observed) { yield return new AccountUsageObservedEvent(observed); }
        await ProviderHttp.ThrowIfUnsuccessfulAsync(
            response,
            Transport,
            credential,
            "Anthropic Messages request",
            cancellationToken);

        if (!stream)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            foreach (LlmStreamEvent completeEvent in ReadCompleteResponse(
                         body, response, connection, request, enforcement, credential, capabilities.SupportsReasoningText))
            {
                yield return completeEvent;
            }

            yield break;
        }

        await using Stream events = await response.Content.ReadAsStreamAsync(cancellationToken);
        var text = new StringBuilder();
        int? inputTokens = null;
        int? cachedTokens = null;
        int? cacheWriteTokens = null;
        int? outputTokens = null;
        string? responseId = null;
        string? stopReason = null;
        string? containerId = null;
        string? refusalCategory = null;
        string? refusalExplanation = null;
        string? lastEvent = null;
        var toolCalls = new Dictionary<int, AnthropicToolCallBuilder>();
        var streamedContent = new AnthropicContentStream();
        var usageDeltas = new UsageDeltas(capabilities.SupportsUsageDeltas);

        await foreach (string data in Sse.ReadDataPayloadsAsync(events, cancellationToken))
        {
            lastEvent = data;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(data);
            }
            catch (JsonException exception)
            {
                throw ProtocolException(
                    "Anthropic Messages returned a malformed stream event.",
                    response,
                    data,
                    credential,
                    exception);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                switch (LlmJson.GetString(root, "type"))
                {
                    case "message_start":
                        if (LlmJson.GetObject(root, "message") is { } message)
                        {
                            responseId = LlmJson.GetString(message, "id");
                            if (message.TryGetProperty("container", out _)) containerId = ReadContainer(message, connection.Transport);
                            inputTokens = ReadToken(message, "usage", "input_tokens") ?? inputTokens;
                            cachedTokens = ReadToken(message, "usage", "cache_read_input_tokens") ?? cachedTokens;
                            cacheWriteTokens = ReadToken(message, "usage", "cache_creation_input_tokens") ?? cacheWriteTokens;
                        }

                        if (usageDeltas.Next(AnthropicUsage.Create(inputTokens, outputTokens, cachedTokens, cacheWriteTokens)) is { } initialUsage)
                        {
                            yield return initialUsage;
                        }

                        break;
                    case "content_block_delta":
                        streamedContent.ObserveDelta(root);
                        if (LlmJson.GetObject(root, "delta") is { } delta
                            && LlmJson.GetString(delta, "type") == "text_delta")
                        {
                            string value = LlmJson.GetString(delta, "text") ?? string.Empty;
                            if (value.Length > 0)
                            {
                                text.Append(value);
                                yield return new LlmTextDeltaEvent(value);
                            }
                        }
                        else if (LlmJson.GetObject(root, "delta") is { } thinkingDelta
                            && LlmJson.GetString(thinkingDelta, "type") == "thinking_delta"
                            && LlmJson.GetString(thinkingDelta, "thinking") is { Length: > 0 } thinking)
                        {
                            if (capabilities.SupportsReasoningText) { yield return new LlmReasoningDeltaEvent(thinking, true); }
                        }
                        else if (LlmJson.GetObject(root, "delta") is { } toolDelta
                            && LlmJson.GetString(toolDelta, "type") == "input_json_delta")
                        {
                            int? index = LlmJson.GetInt(root, "index");
                            string partialJson = LlmJson.GetString(toolDelta, "partial_json")
                                ?? string.Empty;
                            if (index is not null
                                && partialJson.Length > 0
                                && toolCalls.TryGetValue(index.Value, out AnthropicToolCallBuilder? builder))
                            {
                                builder.Arguments.Append(partialJson);
                                yield return new LlmToolCallArgumentsDeltaEvent(builder.Id, partialJson);
                            }
                        }

                        break;
                    case "content_block_start":
                        streamedContent.ObserveStart(root);
                        if (LlmJson.GetObject(root, "content_block") is { } initialBlock
                            && LlmJson.GetString(initialBlock, "type") == "text"
                            && LlmJson.GetString(initialBlock, "text") is { Length: > 0 } initialText)
                        {
                            text.Append(initialText);
                            yield return new LlmTextDeltaEvent(initialText);
                        }
                        if (LlmJson.GetObject(root, "content_block") is { } contentBlock
                            && LlmJson.GetString(contentBlock, "type") == "tool_use"
                            && LlmJson.GetInt(root, "index") is int toolIndex
                            && LlmJson.GetString(contentBlock, "id") is { Length: > 0 } toolId
                            && LlmJson.GetString(contentBlock, "name") is { Length: > 0 } toolName)
                        {
                            var builder = new AnthropicToolCallBuilder(toolId, toolName);
                            if (contentBlock.TryGetProperty("input", out JsonElement initialInput)
                                && initialInput.ValueKind == JsonValueKind.Object
                                && initialInput.GetRawText() != "{}")
                            {
                                builder.InitialArguments = initialInput.GetRawText();
                            }

                            toolCalls[toolIndex] = builder;
                            yield return new LlmToolCallStartedEvent(toolId, toolName, toolIndex);
                        }

                        break;
                    case "content_block_stop":
                        streamedContent.ObserveStop(root);
                        if (LlmJson.GetInt(root, "index") is int completedIndex
                            && toolCalls.TryGetValue(completedIndex, out AnthropicToolCallBuilder? completedBuilder)
                            && !completedBuilder.Completed)
                        {
                            completedBuilder.Completed = true;
                            yield return new LlmToolCallCompletedEvent(completedBuilder.Build());
                        }

                        break;
                    case "message_delta":
                        if (LlmJson.GetObject(root, "delta") is { } messageDelta)
                        {
                            stopReason = LlmJson.GetString(messageDelta, "stop_reason") ?? stopReason;
                            if (messageDelta.TryGetProperty("container", out _)) containerId = ReadContainer(messageDelta, connection.Transport);
                            ReadStopDetails(messageDelta, ref refusalCategory, ref refusalExplanation);
                        }

                        ReadStopDetails(root, ref refusalCategory, ref refusalExplanation);
                        outputTokens = ReadToken(root, "usage", "output_tokens") ?? outputTokens;
                        inputTokens = ReadToken(root, "usage", "input_tokens") ?? inputTokens;
                        cachedTokens = ReadToken(root, "usage", "cache_read_input_tokens") ?? cachedTokens;
                        cacheWriteTokens = ReadToken(root, "usage", "cache_creation_input_tokens") ?? cacheWriteTokens;
                        if (usageDeltas.Next(AnthropicUsage.Create(inputTokens, outputTokens, cachedTokens, cacheWriteTokens)) is { } runningUsage)
                        {
                            yield return runningUsage;
                        }
                        if (string.Equals(stopReason, "refusal", StringComparison.Ordinal)
                            && refusalExplanation is { Length: > 0 } declined)
                        {
                            yield return new LlmRefusalDeltaEvent(declined);
                        }

                        break;
                    case "error":
                        throw ProviderError(root, response, data, credential);
                    case "message_stop":
                        LlmToolCall[] completedToolCalls = toolCalls
                            .OrderBy(pair => pair.Key)
                            .Select(pair => pair.Value.Build())
                            .ToArray();
                        foreach (AnthropicToolCallBuilder builder in toolCalls.Values.Where(value => !value.Completed))
                        {
                            builder.Completed = true;
                            yield return new LlmToolCallCompletedEvent(builder.Build());
                        }

                        yield return new LlmChatCompletionEvent(BuildResult(
                            connection,
                            request,
                            enforcement,
                            text.ToString(),
                            stopReason,
                            refusalCategory,
                            refusalExplanation,
                            completedToolCalls,
                            AnthropicUsage.Create(inputTokens, outputTokens, cachedTokens, cacheWriteTokens),
                            responseId,
                            streamedContent.BuildContent(),
                            containerId,
                            response,
                            lastEvent,
                            credential));
                        yield break;
                }
            }
        }

        throw ProtocolException(
            "Anthropic Messages ended without a terminal event.",
            response,
            lastEvent ?? string.Empty,
            credential);
    }

    protected virtual LlmAccountUsageSnapshot? ObserveAccountUsage(HttpResponseMessage response) =>
        AnthropicRateLimitHeaders.Parse(response.Headers, DateTimeOffset.UtcNow);

    /// <summary>Maps a response that arrived as one Message object.</summary>
    private List<LlmStreamEvent> ReadCompleteResponse(
        string body,
        HttpResponseMessage response,
        LlmConnection connection,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        LlmCredential? credential,
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
                "Anthropic Messages returned a malformed response.",
                response,
                body,
                credential,
                exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string? type = root.ValueKind == JsonValueKind.Object ? LlmJson.GetString(root, "type") : null;
            if (type == "error")
            {
                throw ProviderError(root, response, body, credential);
            }

            if (type != "message"
                || !root.TryGetProperty("content", out JsonElement content)
                || content.ValueKind != JsonValueKind.Array)
            {
                throw ProtocolException("Anthropic Messages returned a malformed response.", response, body, credential);
            }

            var text = new StringBuilder();
            var reasoning = new List<LlmReasoningText>();
            var toolCalls = new List<LlmToolCall>();
            foreach (JsonElement block in content.EnumerateArray())
            {
                switch (LlmJson.GetString(block, "type"))
                {
                    case "text":
                        text.Append(LlmJson.GetString(block, "text"));
                        break;
                    case "thinking" when LlmJson.GetString(block, "thinking") is { Length: > 0 } thinking:
                        if (reasoningText)
                        { reasoning.Add(new LlmReasoningText(thinking, IsSummary: true)); }
                        break;
                    case "tool_use":
                        string id = LlmJson.GetString(block, "id") ?? string.Empty;
                        string name = LlmJson.GetString(block, "name") ?? string.Empty;
                        if (id.Length == 0 || name.Length == 0)
                        {
                            throw ProtocolException("Anthropic Messages returned an incomplete tool call.", response, body, credential);
                        }

                        toolCalls.Add(new LlmToolCall(
                            id,
                            name,
                            block.TryGetProperty("input", out JsonElement input) && input.ValueKind == JsonValueKind.Object
                                ? input.GetRawText()
                                : "{}"));
                        break;
                }
            }

            string? refusalCategory = null;
            string? refusalExplanation = null;
            ReadStopDetails(root, ref refusalCategory, ref refusalExplanation);
            LlmChatResult result = BuildResult(
                connection,
                request,
                enforcement,
                text.ToString(),
                LlmJson.GetString(root, "stop_reason"),
                refusalCategory,
                refusalExplanation,
                [.. toolCalls],
                AnthropicUsage.Create(
                    ReadToken(root, "usage", "input_tokens"),
                    ReadToken(root, "usage", "output_tokens"),
                    ReadToken(root, "usage", "cache_read_input_tokens"),
                    ReadToken(root, "usage", "cache_creation_input_tokens")),
                LlmJson.GetString(root, "id"),
                content.GetArrayLength() == 0 ? null : JsonNode.Parse(content.GetRawText())!.AsArray(),
                ReadContainer(root, connection.Transport),
                response,
                body,
                credential);
            return [.. CompleteResponseEvents.From(result, reasoning)];
        }
    }

    private static LlmChatResult BuildResult(
        LlmConnection connection,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        string text,
        string? stopReason,
        string? refusalCategory,
        string? refusalExplanation,
        LlmToolCall[] toolCalls,
        LlmUsage? usage,
        string? responseId,
        JsonArray? content,
        string? containerId,
        HttpResponseMessage response,
        string? rawFinal,
        LlmCredential? credential)
    {
        bool refused = string.Equals(stopReason, "refusal", StringComparison.Ordinal);
        bool paused = string.Equals(stopReason, "pause_turn", StringComparison.Ordinal);
        if (paused && toolCalls.Length > 0)
            throw new LlmProviderException("Anthropic returned pause_turn with pending client tools; a mixed client/server-tool reply must end with tool_use.",
                connection.Transport, response.StatusCode);
        bool incomplete = paused || stopReason is "max_tokens" or "model_context_window_exceeded";
        bool requiresAction = toolCalls.Length > 0
            || string.Equals(stopReason, "tool_use", StringComparison.Ordinal);
        LlmCompletionStatus completionStatus = refused
            ? LlmCompletionStatus.Refused
            : incomplete
                ? LlmCompletionStatus.Incomplete
                : requiresAction
                    ? LlmCompletionStatus.RequiresAction
                    : stopReason is "end_turn" or "stop_sequence"
                        ? LlmCompletionStatus.Completed : LlmCompletionStatus.Unknown;
        LlmStructuredOutputValidation? validation = request.StructuredOutput is { } structured
            && enforcement is not null
            && !requiresAction
                ? refused
                    ? new LlmStructuredOutputValidation(
                        enforcement.Value,
                        false,
                        false,
                        ["Structured output was not evaluated because the provider refused the request."])
                    : StructuredOutput.Validate(text, structured, enforcement.Value)
                : null;
        return new LlmChatResult
        {
            Text = text,
            Refusal = refused ? refusalExplanation ?? string.Empty : null,
            RefusalCategory = refused ? refusalCategory : null,
            Status = completionStatus,
            FinishReason = stopReason,
            IncompleteReason = incomplete ? stopReason : null,
            Usage = usage,
            ProviderResponseId = responseId,
            ToolCalls = completionStatus == LlmCompletionStatus.RequiresAction ? toolCalls : [],
            ContinuationState = (paused || completionStatus is LlmCompletionStatus.Completed or LlmCompletionStatus.RequiresAction)
                && content is not null
                    ? AnthropicContinuation.WithContainer(ProviderContinuationStateCodec.Encode(connection, request.ModelId, content), containerId) : null,
            StructuredOutput = validation,
            Diagnostics = new LlmProviderDiagnostics(
                (int)response.StatusCode,
                ProviderHttp.ReadRequestId(response),
                rawFinal is null ? null : SecretRedactor.Sanitize(rawFinal, credential))
        };
    }

    private static string? ReadContainer(JsonElement root, LlmTransport transport)
    {
        if (!root.TryGetProperty("container", out JsonElement container) || container.ValueKind == JsonValueKind.Null) return null;
        if (container.ValueKind != JsonValueKind.Object || LlmJson.GetString(container, "id") is not { } id || string.IsNullOrWhiteSpace(id))
            throw new LlmProviderException("Anthropic returned an invalid code-execution container identity.", transport);
        return id;
    }

    private LlmProviderException ProviderError(
        JsonElement root,
        HttpResponseMessage response,
        string raw,
        LlmCredential? credential)
    {
        JsonElement error = LlmJson.GetObject(root, "error") ?? root;
        return new LlmProviderException(
            SecretRedactor.Sanitize(
                LlmJson.GetString(error, "message") ?? "Anthropic returned a stream error.",
                credential),
            Transport,
            response.StatusCode,
            LlmJson.GetString(error, "type"),
            ProviderHttp.ReadRequestId(response),
            sanitizedResponseBody: SecretRedactor.Sanitize(raw, credential));
    }

    public override async Task<IReadOnlyList<LlmModel>> ListModelsAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        _ = GetCapabilities(connection);
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        var models = new List<LlmModel>();
        string? afterId = null;

        while (true)
        {
            string query = $"?limit={ModelPageSize}"
                + (afterId is null ? string.Empty : $"&after_id={Uri.EscapeDataString(afterId)}");
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                BuildApiUri(connection, $"models{query}"));
            ApplyAnthropicHeaders(request, connection, credential);
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            await ProviderHttp.ThrowIfUnsuccessfulAsync(
                response,
                Transport,
                credential,
                "Anthropic model discovery",
                cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException exception)
            {
                throw ProtocolException(
                    "Anthropic model discovery returned invalid JSON.",
                    response,
                    body,
                    credential,
                    exception);
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("data", out JsonElement data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                foreach (JsonElement entry in data.EnumerateArray())
                {
                    string id = LlmJson.GetString(entry, "id") ?? string.Empty;
                    if (id.Length == 0)
                    {
                        continue;
                    }
                    var model = new LlmModel(id, LlmJson.GetString(entry, "display_name") ?? id)
                    {
                        CatalogEntry = entry.Clone(),
                        MaxContextTokens = entry.TryGetProperty("max_input_tokens", out JsonElement input)
                            && input.ValueKind == JsonValueKind.Number && input.TryGetInt64(out long inputTokens) ? inputTokens : null,
                        MaxOutputTokens = entry.TryGetProperty("max_tokens", out JsonElement output)
                            && output.ValueKind == JsonValueKind.Number && output.TryGetInt64(out long outputTokens) ? outputTokens : null
                    };
                    // The listed capabilities come from this entry, as generation's do once the catalog is kept.
                    models.Add(model with { Capabilities = GetCapabilities(connection with { Catalog = [model] }, id) });
                }

                // Anthropic pages model discovery. Without this the list stops at the first page.
                bool hasMore = root.TryGetProperty("has_more", out JsonElement more)
                    && more.ValueKind == JsonValueKind.True;
                afterId = LlmJson.GetString(root, "last_id");
                if (!hasMore || string.IsNullOrEmpty(afterId))
                {
                    break;
                }
            }
        }

        return [.. models.OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    protected virtual HttpRequestMessage CreateHttpRequest(LlmConnection connection, LlmChatRequest request, JsonObject payload, bool stream)
    {
        var result = new HttpRequestMessage(HttpMethod.Post, BuildApiUri(connection, "messages")) { Content = JsonContent.Create(payload) };
        ApplyAnthropicHeaders(result, connection, LlmCredential.Parse(connection.Credential));
        return result;
    }

    protected virtual HttpRequestMessage CreateTokenCountRequest(LlmConnection connection, LlmChatRequest request, JsonObject payload)
    {
        var result = new HttpRequestMessage(HttpMethod.Post, BuildApiUri(connection, "messages/count_tokens"))
        { Content = JsonContent.Create(payload) };
        ApplyAnthropicHeaders(result, connection, LlmCredential.Parse(connection.Credential));
        return result;
    }

    protected virtual string ReasoningModelId(string modelId) => modelId;

    protected virtual JsonObject BuildPayload(
        LlmConnection connection,
        LlmChatRequest request,
        Func<LlmToolOutput, ProjectedToolOutput> projectToolOutput,
        bool stream)
    {
        var system = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            system.Add(new JsonObject { ["type"] = "text", ["text"] = request.Instructions });
        }

        var messages = new JsonArray();
        string? containerId = null;
        foreach (LlmChatMessage message in request.Messages)
        {
            if (message.ContinuationState is not null)
            {
                JsonArray retained = ProviderContinuationStateCodec.DecodeMessage(message, connection, request.ModelId);
                containerId = AnthropicContinuation.ReadPayload(message.ContinuationState.Payload).ContainerId ?? containerId;
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = retained
                });
            }
            else if (message.Role is "system" or "developer")
            {
                system.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
            }
            else if (message.Role is "user" or "assistant" || message.ToolResults.Count > 0)
            {
                var content = new JsonArray();
                foreach (LlmToolResult result in ClientTools.Functions(message.ToolResults))
                {
                    ProjectedToolOutput projected = projectToolOutput(result.Output);
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = result.ToolCallId,
                        ["content"] = BuildToolResultContent(projected),
                        ["is_error"] = projected.IsError
                    });
                }

                if (message.Content.Length > 0)
                {
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
                }

                foreach (LlmAttachment attachment in message.Attachments.Select(LlmAttachments.RequireBinary))
                {
                    content.Add(attachment.Kind switch
                    {
                        LlmBinaryKind.Image or LlmBinaryKind.Document =>
                            AnthropicAttachments.BuildContentBlock(attachment),
                        _ => throw new ArgumentOutOfRangeException(nameof(request))
                    });
                }

                foreach (LlmToolCall call in ClientTools.Functions(message.ToolCalls))
                {
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = call.Id,
                        ["name"] = call.Name,
                        ["input"] = JsonNode.Parse(call.ArgumentsJson)
                    });
                }

                messages.Add(new JsonObject
                {
                    ["role"] = message.ToolResults.Count > 0 ? "user" : message.Role,
                    ["content"] = content
                });
            }
        }

        var payload = new JsonObject
        {
            ["model"] = request.ModelId,
            ["max_tokens"] = request.MaxOutputTokens ?? DefaultMaxOutputTokens,
            ["messages"] = messages,
            ["stream"] = stream
        };
        if (containerId is not null) payload["container"] = containerId;
        AnthropicReasoningFields fields = AnthropicModelProfiles.Fields(
            AnthropicModelProfiles.Resolve(
                ReasoningModelId(request.ModelId),
                ILlmCatalogCapabilities.EntryFor(connection, request.ModelId)),
            request.ReasoningLevel,
            request.MaxOutputTokens ?? DefaultMaxOutputTokens,
            GetCapabilities(connection, request.ModelId).SupportsReasoningText);
        if (fields.Thinking is { } thinking)
        {
            payload["thinking"] = thinking;
        }
        if (fields.OutputConfig is { } outputConfig)
        {
            if (payload["output_config"] is JsonObject existing)
            {
                // Keep fields another feature already wrote, such as a response format.
                foreach ((string name, JsonNode? value) in outputConfig.ToArray())
                {
                    outputConfig.Remove(name);
                    existing[name] = value;
                }
            }
            else
            {
                payload["output_config"] = outputConfig;
            }
        }
        if (fields.MaxTokens is int maxTokens)
        {
            payload["max_tokens"] = maxTokens;
        }
        if (AnthropicSchemas.Enforcement(request, GetCapabilities(connection, request.ModelId))
            == LlmStructuredOutputEnforcement.ProviderJsonSchema)
        {
            JsonObject output = payload["output_config"]?.AsObject() ?? new JsonObject();
            output["format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["schema"] = JsonNode.Parse(request.StructuredOutput!.Schema.GetRawText())
            };
            if (payload["output_config"] is null) payload["output_config"] = output;
        }
        if (system.Count > 0)
        {
            payload["system"] = system;
        }

        if (request.Temperature is double temperature)
        {
            payload["temperature"] = temperature;
        }
        if (request.TopP is { } topP) { payload["top_p"] = topP; }
        if (request.TopK is { } topK) { payload["top_k"] = topK; }
        if (request.StopSequences.Count > 0) { payload["stop_sequences"] = JsonSerializer.SerializeToNode(request.StopSequences); }

        JsonArray tools = AnthropicTools.Resolve(connection, request).Build(ClientTools.Functions(request.Tools));
        if (tools.Count > 0) payload["tools"] = tools;

        if (tools.Count > 0
            && (request.ToolChoice is not null || request.AllowParallelToolCalls is not null))
        {
            LlmToolChoice choice = request.ToolChoice ?? new LlmToolChoice();
            var mappedChoice = choice.Mode switch
            {
                LlmToolChoiceMode.Auto => new JsonObject { ["type"] = "auto" },
                LlmToolChoiceMode.None => new JsonObject { ["type"] = "none" },
                LlmToolChoiceMode.Required => new JsonObject { ["type"] = "any" },
                LlmToolChoiceMode.Specific => new JsonObject
                {
                    ["type"] = "tool",
                    ["name"] = choice.Name
                },
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };
            if (request.AllowParallelToolCalls is bool allowParallelToolCalls)
            {
                mappedChoice["disable_parallel_tool_use"] = !allowParallelToolCalls;
            }

            payload["tool_choice"] = mappedChoice;
        }

        AnthropicToolContinuation.Validate(payload);
        RequestJson.ApplyProviderOptions(payload, connection, request, AnthropicTools.OptionName);
        return payload;
    }

    /// <summary>A single text part stays a string; anything richer is the ordered block list.</summary>
    private static JsonNode BuildToolResultContent(ProjectedToolOutput projected)
    {
        if (projected.Parts is [SentText only])
        {
            return JsonValue.Create(only.Text);
        }

        var content = new JsonArray();
        foreach (SentToolPart part in projected.Parts)
        {
            content.Add(part is SentText text
                ? new JsonObject { ["type"] = "text", ["text"] = text.Text }
                : AnthropicAttachments.BuildToolPart(part));
        }
        return content;
    }

    private static void ApplyAnthropicHeaders(
        HttpRequestMessage request,
        LlmConnection connection,
        LlmCredential? credential)
    {
        ProviderHttp.ApplyHeaders(request, connection, applyApiKeyAsBearer: false);
        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        if (!string.IsNullOrWhiteSpace(credential?.ApiKey))
        {
            if (!request.Headers.Contains("x-api-key"))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", credential.ApiKey);
            }
        }
        else if (!request.Headers.Contains("x-api-key"))
        {
            throw new InvalidOperationException("Anthropic Messages requires an API key.");
        }

        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static Uri BuildApiUri(LlmConnection connection, string path)
    {
        string endpoint = connection.Endpoint.TrimEnd('/');
        return new Uri(
            endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? $"{endpoint}/{path}"
                : $"{endpoint}/v1/{path}",
            UriKind.Absolute);
    }

    private static void ReadStopDetails(
        JsonElement element,
        ref string? category,
        ref string? explanation)
    {
        if (LlmJson.GetObject(element, "stop_details") is { } details)
        {
            category = LlmJson.GetString(details, "category") ?? category;
            explanation = LlmJson.GetString(details, "explanation") ?? explanation;
        }
    }

    private static int? ReadToken(JsonElement root, string objectName, string propertyName) =>
        LlmJson.GetObject(root, objectName) is { } usage
            ? LlmJson.GetInt(usage, propertyName)
            : null;

    private sealed class AnthropicToolCallBuilder(string id, string name)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public string? InitialArguments { get; set; }

        public StringBuilder Arguments { get; } = new();

        public bool Completed { get; set; }

        public LlmToolCall Build() => new(
            Id,
            Name,
            Arguments.Length > 0 ? Arguments.ToString() : InitialArguments ?? "{}");
    }
}
