using System.Text.Json;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal abstract class LlmClientBase(HttpClient http) : ILlmClient, ILlmResourcePreparationClient
{
    protected HttpClient Http { get; } = http;

    public abstract LlmTransport Transport { get; }

    public abstract LlmProviderDefinition ProviderDefinition { get; }

    protected abstract LlmProviderCapabilities DefaultCapabilities { get; }

    /// <summary>True when the provider has a non-streaming request that this adapter sends while streaming is off.</summary>
    protected virtual bool OffersNonStreamingRequests => false;

    protected virtual bool OffersUsageDeltas => DefaultCapabilities.SupportsUsageDeltas;

    public virtual LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        EnsureTransport(connection);
        return LlmCapabilityOverrideLookup.Apply(DefaultCapabilities, connection, modelId, OffersUsageDeltas);
    }

    public abstract IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConnection connection,
        LlmChatRequest request,
        CancellationToken cancellationToken = default);

    public virtual async Task<IReadOnlyList<LlmModel>> ListModelsAsync(
        LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        LlmProviderCapabilities capabilities = GetCapabilities(connection);
        if (!capabilities.SupportsModelListing)
        {
            return [];
        }

        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        IReadOnlyList<LlmModel> result;
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.BuildUri(connection, "models"));
            ProviderHttp.ApplyHeaders(request, connection);

            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
            string[] secrets = request.Headers.SelectMany(header => header.Value).ToArray();
            await ProviderHttp.ThrowIfUnsuccessfulAsync(
                response,
                Transport,
                credential,
                "Model discovery",
                secrets,
                cancellationToken);

            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                JsonElement models = root.TryGetProperty("data", out JsonElement data)
                    && data.ValueKind == JsonValueKind.Array
                        ? data
                        : root.TryGetProperty("models", out JsonElement alternate)
                            && alternate.ValueKind == JsonValueKind.Array
                                ? alternate
                                : default;

                if (models.ValueKind != JsonValueKind.Array)
                {
                    result = [];
                }
                else
                {
                    var results = new List<LlmModel>();
                    foreach (JsonElement model in models.EnumerateArray())
                    {
                        string? id = LlmJson.GetString(model, "id");
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }

                        string displayName = LlmJson.GetString(model, "name")
                            ?? LlmJson.GetString(model, "display_name")
                            ?? id;
                        results.Add(new LlmModel(id, displayName, LlmJson.GetString(model, "owned_by"))
                        {
                            MaxContextTokens = PositiveLong(model, "context_length")
                                ?? PositiveLong(LlmJson.GetObject(model, "top_provider"), "context_length")
                                ?? PositiveLong(model, "max_model_len"),
                            MaxOutputTokens = PositiveLong(LlmJson.GetObject(model, "top_provider"), "max_completion_tokens")
                        });
                    }

                    result = [.. results.OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)];
                }
            }
            catch (JsonException exception)
            {
                throw ProtocolException(
                    "Model discovery returned invalid JSON.",
                    response,
                    body,
                    credential,
                    exception, secrets);
            }
        }
        return consumption.Publish(result);
    }

    private static long? PositiveLong(JsonElement? value, string field) =>
        value is { ValueKind: JsonValueKind.Object } item && item.TryGetProperty(field, out JsonElement number)
        && number.ValueKind == JsonValueKind.Number && number.TryGetInt64(out long count) && count > 0 ? count : null;

    protected void ValidateRequest(
        LlmConnection connection,
        LlmChatRequest request,
        LlmProviderCapabilities capabilities,
        IReadOnlySet<string>? nativeToolNames = null)
    {
        EnsureTransport(connection);
        ProviderControlPolicy.Validate(connection, request);
        ExplicitCaches.Validate(request, capabilities.SupportsExplicitCache);
        RequestJson.ValidateSamplingOptions(connection, request);
        HostedTools.Validate(request, capabilities);
        ClientTools.Validate(request, capabilities.SupportsShellTools);
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            throw new ArgumentException("A model id is required.", nameof(request));
        }

        if (!capabilities.SupportsStreaming && !OffersNonStreamingRequests)
        {
            throw new NotSupportedException(
                $"{ProviderDefinition.DisplayName} has no non-streaming request, so streaming cannot be turned off for this connection.");
        }

        ValidateSampling(request, capabilities);
        LlmReasoningEffort.Validate(request.ReasoningLevel, capabilities);
        ValidateOutputLimit(request, capabilities);

        StructuredOutput.ValidateRequest(request);

        ValidatePromptAttachments(request, capabilities, SupportsResourcePreparation);
        ValidateToolUse(request, capabilities, nativeToolNames);
        ValidateAdditionalRequest(connection, request);
    }

    protected virtual bool SupportsResourcePreparation => false;
    /// <summary>Reports the adapter's implemented logical-resource preparation lane.</summary>
    public bool CanPrepareResources => SupportsResourcePreparation;

    protected virtual void ValidateAdditionalRequest(LlmConnection connection, LlmChatRequest request) { }

    internal static void ValidateSampling(LlmChatRequest request, LlmProviderCapabilities capabilities)
    {
        if (request.Temperature is not null && !capabilities.SupportsTemperature)
        { throw new NotSupportedException("The selected connection does not support temperature."); }
        if (request.Temperature is double temperature && !double.IsFinite(temperature))
        { throw new ArgumentOutOfRangeException(nameof(request), "Temperature must be finite."); }
        if (capabilities.MaxStopSequences < 0)
        { throw new ArgumentOutOfRangeException(nameof(capabilities), "The stop-sequence limit cannot be negative."); }
        if (request.TopP is not null && !capabilities.SupportsTopP)
        { throw new NotSupportedException("The selected model does not support top-p."); }
        if (request.TopP is { } topP && (!double.IsFinite(topP) || topP is <= 0 or > 1))
        { throw new ArgumentOutOfRangeException(nameof(request), "TopP must be finite and in (0, 1]."); }
        if (request.TopK is not null && !capabilities.SupportsTopK)
        { throw new NotSupportedException("The selected model does not support top-k."); }
        if (request.TopK is <= 0)
        { throw new ArgumentOutOfRangeException(nameof(request), "TopK must be positive."); }
        if (request.Seed is not null && !capabilities.SupportsSeed)
        { throw new NotSupportedException("The selected model does not support a sampling seed."); }
        if (request.Seed is < 0)
        { throw new ArgumentOutOfRangeException(nameof(request), "Seed must be non-negative."); }
        if (request.PresencePenalty is not null && !capabilities.SupportsPresencePenalty)
        { throw new NotSupportedException("The selected model does not support a presence penalty."); }
        if (request.FrequencyPenalty is not null && !capabilities.SupportsFrequencyPenalty)
        { throw new NotSupportedException("The selected model does not support a frequency penalty."); }
        if (request.PresencePenalty is { } presence && !double.IsFinite(presence)
            || request.FrequencyPenalty is { } frequency && !double.IsFinite(frequency))
        { throw new ArgumentOutOfRangeException(nameof(request), "Sampling penalties must be finite."); }
        ArgumentNullException.ThrowIfNull(request.StopSequences);
        if (request.StopSequences.Any(string.IsNullOrEmpty))
        { throw new ArgumentException("Stop sequences must contain non-empty strings.", nameof(request)); }
        if (request.StopSequences.Count > capabilities.MaxStopSequences)
        { throw new NotSupportedException($"The selected model accepts at most {capabilities.MaxStopSequences} stop sequences."); }
    }

    internal static void ValidateOutputLimit(LlmChatRequest request, LlmProviderCapabilities capabilities)
    {
        if (request.MaxOutputTokens is <= 0)
        { throw new ArgumentOutOfRangeException(nameof(request), "Maximum output tokens must be positive."); }
        if (request.MaxOutputTokens is not null
            && capabilities.OutputLimitEnforcement == Common.LLM.LlmOutputLimitEnforcement.Unsupported)
        { throw new NotSupportedException("This route cannot enforce MaxOutputTokens; an output reserve is budgeting only."); }
    }

    internal static void ValidatePromptAttachments(
        LlmChatRequest request,
        LlmProviderCapabilities capabilities,
        bool supportsResources = false)
    {
        LlmAttachments.ValidateReferences(request, () => capabilities.SupportsFileReferences, supportsResources);
        foreach (LlmChatMessage message in request.Messages)
        {
            if (message.Attachments.Count > 0
                && !string.Equals(message.Role, "user", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Prompt attachments are supported only on user messages.",
                    nameof(request));
            }

            LlmAttachments.Validate(
                message.Attachments,
                capabilities.SupportsPromptImages,
                capabilities.SupportsPromptDocuments,
                nameof(request),
                capabilities.SupportsFileReferences || supportsResources);
        }
    }

    internal static void ValidateToolUse(
        LlmChatRequest request,
        LlmProviderCapabilities capabilities,
        IReadOnlySet<string>? nativeToolNames = null)
    {
        bool hasToolHistory = request.Messages.Any(message =>
            message.ToolCalls.Count > 0 || message.ToolResults.Count > 0);
        // A text transcript carries tool history without asking the provider to run host tools.
        bool requestsToolUse = request.Tools.Count > 0
            || request.ToolChoice is not null
            || request.AllowParallelToolCalls is not null
            || (hasToolHistory && capabilities.ToolHistory == LlmToolHistory.Structured);

        if (requestsToolUse && !capabilities.SupportsToolUse)
        {
            throw new NotSupportedException("The selected connection does not support host-executed tools.");
        }

        if (!Enum.IsDefined(capabilities.Tools.StrictInputSchema))
        { throw new ArgumentException("Invalid strict tool-schema capability.", nameof(capabilities)); }
        // Unknown deliberately permits an attempt. Never turn a host opt-in into a non-strict request.
        if (request.Tools.Any(tool => tool is LlmToolDefinition { Strict: true })
            && capabilities.Tools.StrictInputSchema == LlmStrictInputSchemaSupport.Unsupported)
        { throw new NotSupportedException("The selected route cannot enforce strict tool input schemas."); }

        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (LlmClientToolDefinition definition in request.Tools)
        {
            if (definition is LlmShellTool) continue;
            if (definition is not LlmToolDefinition tool) throw new NotSupportedException("Unknown client tool definition.");
            if (string.IsNullOrWhiteSpace(tool.Name)
                || tool.InputSchema.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "Every tool requires a name and a JSON object input schema.",
                    nameof(request));
            }

            if (!toolNames.Add(tool.Name))
            {
                throw new ArgumentException($"Tool names must be unique: '{tool.Name}'.", nameof(request));
            }
        }

        if (nativeToolNames is not null)
            foreach (string name in nativeToolNames)
                if (!toolNames.Add(name)) throw new ArgumentException("Tool names must be unique.", nameof(request));

        if (request.ToolChoice is { Mode: LlmToolChoiceMode.Specific } choice
            && (string.IsNullOrWhiteSpace(choice.Name) || !toolNames.Contains(choice.Name)))
        {
            throw new ArgumentException(
                "A specific tool choice must name one of the request tools.",
                nameof(request));
        }

        if (request.ToolChoice is { Mode: not LlmToolChoiceMode.None }
            && toolNames.Count == 0 && request.Tools.Count == 0)
        {
            throw new ArgumentException("Tool choice requires at least one request tool.", nameof(request));
        }

        if (request.AllowParallelToolCalls is not null && toolNames.Count == 0 && request.Tools.Count == 0)
        {
            throw new ArgumentException(
                "Parallel tool-call control requires at least one request tool.",
                nameof(request));
        }

        foreach (LlmChatMessage message in request.Messages)
        {
            if (message.ToolCalls.Count > 0 && message.ToolResults.Count > 0)
            {
                throw new ArgumentException(
                    "A message cannot contain both tool calls and tool results.",
                    nameof(request));
            }

            foreach (LlmClientToolCall observed in message.ToolCalls)
            {
                if (observed is LlmShellCall) continue;
                if (observed is not LlmToolCall call) throw new NotSupportedException("Unknown client tool call.");
                if (string.IsNullOrWhiteSpace(call.Id) || string.IsNullOrWhiteSpace(call.Name))
                {
                    throw new ArgumentException(
                        "Tool-call history requires an id and name.",
                        nameof(request));
                }

                try
                {
                    using JsonDocument arguments = JsonDocument.Parse(call.ArgumentsJson);
                    if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new ArgumentException(
                            "Tool-call arguments must be a JSON object.",
                            nameof(request));
                    }
                }
                catch (JsonException exception)
                {
                    throw new ArgumentException(
                        "Tool-call arguments must contain valid JSON.",
                        nameof(request),
                        exception);
                }
            }

            foreach (LlmClientToolResult observed in message.ToolResults)
            {
                if (observed is LlmShellCallResult) continue;
                if (observed is not LlmToolResult result) throw new NotSupportedException("Unknown client tool result.");
                // Content a route cannot carry is omitted by projection, not rejected here.
                if (string.IsNullOrWhiteSpace(result.ToolCallId) || result.Output is null)
                {
                    throw new ArgumentException(
                        "Tool results require a tool-call id and an output.",
                        nameof(request));
                }
            }
        }
    }

    /// <summary>
    /// Projects a tool output with the connection's support as the route and the model's support as the target.
    /// </summary>
    internal ProjectedToolOutput ProjectToolOutput(LlmConnection connection, string modelId, LlmToolOutput output,
        Func<Common.Resources.InferenceResourceReference, SentToolPart>? resource = null) =>
        ToolOutputProjector.Project(
            output,
            GetCapabilities(connection).RichContentSupported,
            GetCapabilities(connection, modelId).RichContentSupported,
            resource);

    protected LlmProviderException ProtocolException(
        string message,
        HttpResponseMessage response,
        string rawBody,
        LlmCredential? credential,
        JsonException? innerException = null, IEnumerable<string>? extraSecrets = null) => new(
            message,
            Transport,
            response.StatusCode,
            requestId: ProviderHttp.ReadRequestId(response, credential, extraSecrets),
            sanitizedResponseBody: SecretRedactor.Sanitize(rawBody, credential, extraSecrets ?? []),
            innerException: innerException is null ? null : SecretRedactor.Sanitize(innerException));

    protected void EnsureTransport(LlmConnection connection)
    {
        RuntimeInputAdmission.Validate(connection);
        if (connection.Transport != Transport)
        {
            throw new ArgumentException(
                $"A {connection.Transport} connection cannot be used with the {Transport} client.",
                nameof(connection));
        }
    }
}
