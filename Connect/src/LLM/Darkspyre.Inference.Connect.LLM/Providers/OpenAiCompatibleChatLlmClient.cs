using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Streaming adapter for OpenAI-compatible Chat Completions endpoints.</summary>
internal sealed class OpenAiCompatibleChatLlmClient(HttpClient http) : LlmClientBase(http)
{
    public override LlmTransport Transport => LlmTransport.OpenAiCompatibleChat;

    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiCompatible;

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        SupportsStreaming = true,
        SupportsStreamingUsage = true,
        SupportsModelListing = true,
        SupportsJsonSchema = false,
        SupportsJsonObject = false,
        SupportsReasoningEffort = false,
        SupportsTemperature = true,
        SupportsToolUse = true
    };

    public override LlmProviderCapabilities GetCapabilities(
        LlmConnection connection,
        string? modelId = null) => base.GetCapabilities(connection, modelId) with
        {
            SupportsPromptImages = false,
            SupportsPromptDocuments = false,
            SupportsToolResultImages = false,
            SupportsToolResultDocuments = false
        };

    public override async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection, request.ModelId);
        ValidateRequest(connection, request, capabilities);
        LlmStructuredOutputEnforcement? enforcement = request.StructuredOutput is null
            ? null
            : StructuredOutput.ResolveEnforcement(request.StructuredOutput, capabilities);

        JsonObject payload = BuildPayload(connection, request, enforcement, capabilities);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            ProviderHttp.BuildUri(connection, "chat/completions"))
        {
            Content = JsonContent.Create(payload)
        };
        ProviderHttp.ApplyHeaders(httpRequest, connection);

        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        using HttpResponseMessage response = await Http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await ProviderHttp.ThrowIfUnsuccessfulAsync(
            response,
            Transport,
            credential,
            "OpenAI-compatible chat request",
            cancellationToken);

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var text = new StringBuilder();
        var refusal = new StringBuilder();
        string? finishReason = null;
        string? responseId = null;
        LlmChatUsage? usage = null;
        string? lastEvent = null;
        bool sawDone = false;
        var toolCalls = new Dictionary<int, ToolCallBuilder>();

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
                usage = ReadUsage(root) ?? usage;

                if (LlmJson.GetObject(root, "error") is { } error)
                {
                    throw StreamError(error, response, data, credential);
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
                if (contentDelta.Length > 0)
                {
                    text.Append(contentDelta);
                    yield return new LlmTextDeltaEvent(contentDelta);
                }

                string refusalDelta = LlmJson.GetString(delta, "refusal") ?? string.Empty;
                if (refusalDelta.Length > 0)
                {
                    refusal.Append(refusalDelta);
                    yield return new LlmRefusalDeltaEvent(refusalDelta);
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

                        builder.Id ??= LlmJson.GetString(toolCallDelta, "id");
                        string argumentsDelta = string.Empty;
                        if (LlmJson.GetObject(toolCallDelta, "function") is { } function)
                        {
                            builder.Name ??= LlmJson.GetString(function, "name");
                            argumentsDelta = LlmJson.GetString(function, "arguments")
                                ?? string.Empty;
                        }

                        if (!builder.Started
                            && builder.Id is { } callId
                            && builder.Name is { } name)
                        {
                            builder.Started = true;
                            yield return new LlmToolCallStartedEvent(callId, name, index);
                        }

                        if (argumentsDelta.Length > 0)
                        {
                            builder.Arguments.Append(argumentsDelta);
                            if (builder.Id is { } argumentCallId)
                            {
                                yield return new LlmToolCallArgumentsDeltaEvent(
                                    argumentCallId,
                                    argumentsDelta);
                            }
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

        string finalText = text.ToString();
        string finalRefusal = refusal.ToString();
        LlmToolCall[] completedToolCalls = toolCalls
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Build())
            .ToArray();
        foreach (LlmToolCall toolCall in completedToolCalls)
        {
            yield return new LlmToolCallCompletedEvent(toolCall);
        }

        LlmCompletionStatus status = finalRefusal.Length > 0 || finishReason == "content_filter"
            ? LlmCompletionStatus.Refused
            : finishReason == "length"
                ? LlmCompletionStatus.Incomplete
                : completedToolCalls.Length > 0 || finishReason == "tool_calls"
                    ? LlmCompletionStatus.RequiresAction
                    : LlmCompletionStatus.Completed;

        LlmStructuredOutputValidation? validation = null;
        if (request.StructuredOutput is { } structured
            && enforcement is not null
            && status != LlmCompletionStatus.RequiresAction)
        {
            validation = finalRefusal.Length > 0
                ? new LlmStructuredOutputValidation(
                    enforcement.Value,
                    false,
                    false,
                    ["Structured output was not evaluated because the provider refused the request."])
                : StructuredOutput.Validate(finalText, structured, enforcement.Value);
        }

        yield return new LlmCompletionEvent(new LlmChatResult
        {
            Text = finalText,
            Refusal = finalRefusal.Length == 0 ? null : finalRefusal,
            Status = status,
            FinishReason = finishReason,
            IncompleteReason = status == LlmCompletionStatus.Incomplete ? finishReason : null,
            Usage = usage,
            ProviderResponseId = responseId,
            ToolCalls = completedToolCalls,
            StructuredOutput = validation,
            Diagnostics = new LlmProviderDiagnostics(
                (int)response.StatusCode,
                ProviderHttp.ReadRequestId(response),
                lastEvent is null ? null : SecretRedactor.Sanitize(lastEvent, credential))
        });
    }

    private static JsonObject BuildPayload(
        LlmConnection connection,
        LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement,
        LlmProviderCapabilities capabilities)
    {
        if (request.Messages.Any(message => message.ResponseState is not null))
        {
            throw new NotSupportedException(
                "OpenAI-compatible Chat Completions cannot import adapter response state.");
        }

        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.Instructions
            });
        }

        foreach (LlmChatMessage message in request.Messages)
        {
            if (message.ToolResults.Count > 0)
            {
                foreach (LlmToolResult result in message.ToolResults)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = result.ToolCallId,
                        ["content"] = result.Content
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
                ["content"] = message.Content.Length == 0 && message.ToolCalls.Count > 0
                    ? null
                    : message.Content
            };
            if (message.ToolCalls.Count > 0)
            {
                var mappedCalls = new JsonArray();
                foreach (LlmToolCall call in message.ToolCalls)
                {
                    mappedCalls.Add(new JsonObject
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

            messages.Add(mappedMessage);
        }

        var payload = new JsonObject
        {
            ["model"] = request.ModelId,
            ["messages"] = messages,
            ["stream"] = true
        };

        if (capabilities.SupportsStreamingUsage)
        {
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (request.Temperature is double temperature)
        {
            payload["temperature"] = temperature;
        }

        if (request.MaxOutputTokens is int maxOutputTokens)
        {
            payload["max_tokens"] = maxOutputTokens;
        }

        if (!string.IsNullOrWhiteSpace(request.ReasoningEffort))
        {
            payload["reasoning_effort"] = request.ReasoningEffort;
        }

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (LlmToolDefinition tool in request.Tools)
            {
                var function = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["parameters"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
                    ["strict"] = tool.Strict
                };
                if (!string.IsNullOrWhiteSpace(tool.Description))
                {
                    function["description"] = tool.Description;
                }

                tools.Add(new JsonObject
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

        RequestJson.ApplyProviderOptions(payload, connection, request);
        return payload;
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

    private static LlmChatUsage? ReadUsage(JsonElement root)
    {
        if (LlmJson.GetObject(root, "usage") is not { } usage)
        {
            return null;
        }

        return new LlmChatUsage(
            LlmJson.GetInt(usage, "prompt_tokens"),
            LlmJson.GetInt(usage, "completion_tokens"),
            LlmJson.GetInt(usage, "total_tokens"));
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
            sanitizedResponseBody: SecretRedactor.Sanitize(rawEvent, credential));

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();

        public bool Started { get; set; }

        public LlmToolCall Build()
        {
            if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name))
            {
                throw new InvalidOperationException(
                    "The provider returned an incomplete tool-call stream.");
            }

            return new LlmToolCall(
                Id,
                Name,
                Arguments.Length == 0 ? "{}" : Arguments.ToString());
        }
    }
}
