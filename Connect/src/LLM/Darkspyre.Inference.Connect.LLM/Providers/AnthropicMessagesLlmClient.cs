using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Direct Anthropic Messages API adapter for API-key connections.</summary>
internal sealed class AnthropicMessagesLlmClient : LlmClientBase
{
    private const string AnthropicVersion = "2023-06-01";
    private const int DefaultMaxOutputTokens = 8192;
    private const int ModelPageSize = 1000;

    public AnthropicMessagesLlmClient(HttpClient http) : base(http)
    {
    }

    public override LlmTransport Transport => LlmTransport.AnthropicMessages;

    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.AnthropicApiKey;

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        OutputLimitEnforcement = Common.LLM.LlmOutputLimitEnforcement.ProviderEnforced,
        SupportsStreaming = true,
        SupportsStreamingUsage = true,
        SupportsModelListing = true,
        SupportsAccountUsage = false,
        SupportsJsonSchema = false,
        SupportsJsonObject = false,
        SupportsTemperature = false,
        SupportsToolUse = true,
        SupportsPromptImages = true,
        SupportsPromptDocuments = true,
        RichContentSupported = RichContentRoutes.Anthropic
    };

    public override LlmProviderCapabilities GetCapabilities(
        LlmConnection connection,
        string? modelId = null)
    {
        LlmProviderCapabilities capabilities = base.GetCapabilities(connection, modelId);
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if (credential?.OAuth is not null || credential?.Aws is not null)
        {
            throw new NotSupportedException(
                "Anthropic Messages API connections accept only an API key or x-api-key secret header.");
        }
        bool levelsOverridden = connection.CapabilityOverrides?.ReasoningLevels is not null
            || (modelId is not null
                && connection.ModelCapabilityOverrides.TryGetValue(modelId, out LlmCapabilityOverrides? modelOverrides)
                && modelOverrides.ReasoningLevels is not null);
        return capabilities with
        {
            SupportsAccountUsage = false,
            SupportsAgentSessions = false,
            SupportsAgentToolUse = false,
            ReasoningLevels = levelsOverridden ? capabilities.ReasoningLevels : AnthropicReasoning.Levels(modelId)
        };
    }

    public override async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is null
            ? null
            : StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        JsonObject payload = BuildPayload(connection, request, output => ProjectToolOutput(connection, request.ModelId, output));
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            BuildApiUri(connection, "messages"))
        {
            Content = JsonContent.Create(payload)
        };
        ApplyAnthropicHeaders(httpRequest, connection, credential);

        using HttpResponseMessage response = await Http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await ProviderHttp.ThrowIfUnsuccessfulAsync(
            response,
            Transport,
            credential,
            "Anthropic Messages request",
            cancellationToken);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var text = new StringBuilder();
        int? inputTokens = null;
        int? outputTokens = null;
        string? responseId = null;
        string? stopReason = null;
        string? refusalCategory = null;
        string? refusalExplanation = null;
        string? lastEvent = null;
        var toolCalls = new Dictionary<int, AnthropicToolCallBuilder>();
        var streamedContent = new AnthropicContentStream();

        await foreach (string data in Sse.ReadDataPayloadsAsync(stream, cancellationToken))
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
                            inputTokens = ReadToken(message, "usage", "input_tokens") ?? inputTokens;
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
                            ReadStopDetails(messageDelta, ref refusalCategory, ref refusalExplanation);
                        }

                        ReadStopDetails(root, ref refusalCategory, ref refusalExplanation);
                        outputTokens = ReadToken(root, "usage", "output_tokens") ?? outputTokens;
                        if (string.Equals(stopReason, "refusal", StringComparison.Ordinal)
                            && refusalExplanation is { Length: > 0 } declined)
                        {
                            yield return new LlmRefusalDeltaEvent(declined);
                        }

                        break;
                    case "error":
                        JsonElement error = LlmJson.GetObject(root, "error") ?? root;
                        throw new LlmProviderException(
                            SecretRedactor.Sanitize(
                                LlmJson.GetString(error, "message")
                                    ?? "Anthropic returned a stream error.",
                                credential),
                            Transport,
                            response.StatusCode,
                            LlmJson.GetString(error, "type"),
                            ProviderHttp.ReadRequestId(response),
                            sanitizedResponseBody: SecretRedactor.Sanitize(data, credential));
                    case "message_stop":
                        string finalText = text.ToString();
                        bool refused = string.Equals(stopReason, "refusal", StringComparison.Ordinal);
                        bool incomplete = string.Equals(stopReason, "max_tokens", StringComparison.Ordinal);
                        LlmToolCall[] completedToolCalls = toolCalls
                            .OrderBy(pair => pair.Key)
                            .Select(pair => pair.Value.Build())
                            .ToArray();
                        foreach (AnthropicToolCallBuilder builder in toolCalls.Values.Where(value => !value.Completed))
                        {
                            builder.Completed = true;
                            yield return new LlmToolCallCompletedEvent(builder.Build());
                        }

                        bool requiresAction = completedToolCalls.Length > 0
                            || string.Equals(stopReason, "tool_use", StringComparison.Ordinal);
                        LlmCompletionStatus completionStatus = refused
                            ? LlmCompletionStatus.Refused
                            : incomplete
                                ? LlmCompletionStatus.Incomplete
                                : requiresAction
                                    ? LlmCompletionStatus.RequiresAction
                                    : LlmCompletionStatus.Completed;
                        LlmStructuredOutputValidation? validation = request.StructuredOutput is { } structured
                            && enforcement is not null
                            && !requiresAction
                                ? refused
                                    ? new LlmStructuredOutputValidation(
                                        enforcement.Value,
                                        false,
                                        false,
                                        ["Structured output was not evaluated because the provider refused the request."])
                                    : StructuredOutput.Validate(finalText, structured, enforcement.Value)
                                : null;
                        yield return new LlmCompletionEvent(new LlmChatResult
                        {
                            Text = finalText,
                            Refusal = refused ? refusalExplanation ?? string.Empty : null,
                            RefusalCategory = refused ? refusalCategory : null,
                            Status = completionStatus,
                            FinishReason = stopReason,
                            IncompleteReason = incomplete ? stopReason : null,
                            Usage = new LlmChatUsage(
                                inputTokens,
                                outputTokens,
                                inputTokens is null && outputTokens is null
                                    ? null
                                    : (inputTokens ?? 0) + (outputTokens ?? 0)),
                            ProviderResponseId = responseId,
                            ToolCalls = completedToolCalls,
                            ResponseState = incomplete ? null : streamedContent.Encode(connection, request.ModelId),
                            StructuredOutput = validation,
                            Diagnostics = new LlmProviderDiagnostics(
                                (int)response.StatusCode,
                                ProviderHttp.ReadRequestId(response),
                                lastEvent is null
                                    ? null
                                    : SecretRedactor.Sanitize(lastEvent, credential))
                        });
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

                models.AddRange(data.EnumerateArray()
                    .Select(model => new LlmModel(
                        LlmJson.GetString(model, "id") ?? string.Empty,
                        LlmJson.GetString(model, "display_name")
                            ?? LlmJson.GetString(model, "id")
                            ?? string.Empty))
                    .Where(model => model.Id.Length > 0));

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

    private static JsonObject BuildPayload(
        LlmConnection connection,
        LlmChatRequest request,
        Func<LlmToolOutput, ProjectedToolOutput> projectToolOutput)
    {
        var system = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            system.Add(new JsonObject { ["type"] = "text", ["text"] = request.Instructions });
        }

        var messages = new JsonArray();
        foreach (LlmChatMessage message in request.Messages)
        {
            if (message.ResponseState is not null)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = ProviderResponseStateCodec.DecodeMessage(message, connection, request.ModelId)
                });
            }
            else if (message.Role is "system" or "developer")
            {
                system.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content });
            }
            else if (message.Role is "user" or "assistant" || message.ToolResults.Count > 0)
            {
                var content = new JsonArray();
                foreach (LlmToolResult result in message.ToolResults)
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

                foreach (LlmAttachment attachment in message.Attachments)
                {
                    content.Add(attachment.Kind switch
                    {
                        LlmAttachmentKind.Image or LlmAttachmentKind.Document =>
                            AnthropicAttachments.BuildContentBlock(attachment),
                        _ => throw new ArgumentOutOfRangeException(nameof(request))
                    });
                }

                foreach (LlmToolCall call in message.ToolCalls)
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
            ["stream"] = true
        };
        if (request.ReasoningLevel is { } level)
        {
            AnthropicReasoning.Apply(payload, request.ModelId, level, request.MaxOutputTokens ?? DefaultMaxOutputTokens);
        }
        if (system.Count > 0)
        {
            payload["system"] = system;
        }

        if (request.Temperature is double temperature)
        {
            payload["temperature"] = temperature;
        }

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (LlmToolDefinition definition in request.Tools)
            {
                var tool = new JsonObject
                {
                    ["name"] = definition.Name,
                    ["input_schema"] = JsonNode.Parse(definition.InputSchema.GetRawText()),
                    ["strict"] = definition.Strict
                };
                if (!string.IsNullOrWhiteSpace(definition.Description))
                {
                    tool["description"] = definition.Description;
                }

                tools.Add(tool);
            }

            payload["tools"] = tools;
        }

        if (request.Tools.Count > 0
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

        RequestJson.ApplyProviderOptions(payload, connection, request);
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
