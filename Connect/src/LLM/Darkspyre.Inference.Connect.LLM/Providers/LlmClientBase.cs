using System.Text.Json;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

internal abstract class LlmClientBase(HttpClient http) : ILlmClient
{
    protected HttpClient Http { get; } = http;

    public abstract LlmTransport Transport { get; }

    public abstract LlmProviderDefinition ProviderDefinition { get; }

    protected abstract LlmProviderCapabilities DefaultCapabilities { get; }

    public virtual LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        EnsureTransport(connection);
        LlmProviderCapabilities capabilities = DefaultCapabilities.Apply(connection.CapabilityOverrides);
        return modelId is not null
            && connection.ModelCapabilityOverrides.TryGetValue(modelId, out LlmCapabilityOverrides? modelOverrides)
                ? capabilities.Apply(modelOverrides)
                : capabilities;
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
        using var request = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.BuildUri(connection, "models"));
        ProviderHttp.ApplyHeaders(request, connection);

        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        await ProviderHttp.ThrowIfUnsuccessfulAsync(
            response,
            Transport,
            credential,
            "Model discovery",
            cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
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
                return [];
            }

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
                results.Add(new LlmModel(id, displayName, LlmJson.GetString(model, "owned_by")));
            }

            return [.. results.OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)];
        }
        catch (JsonException exception)
        {
            throw ProtocolException(
                "Model discovery returned invalid JSON.",
                response,
                body,
                credential,
                exception);
        }
    }

    protected void ValidateRequest(
        LlmConnection connection,
        LlmChatRequest request,
        LlmProviderCapabilities capabilities)
    {
        EnsureTransport(connection);
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            throw new ArgumentException("A model id is required.", nameof(request));
        }

        if (!capabilities.SupportsStreaming)
        {
            throw new NotSupportedException("The selected connection does not support streaming.");
        }

        if (request.Temperature is not null && !capabilities.SupportsTemperature)
        {
            throw new NotSupportedException("The selected connection does not support temperature.");
        }

        if (request.Temperature is double temperature && !double.IsFinite(temperature))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Temperature must be finite.");
        }

        if (request.MaxOutputTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Maximum output tokens must be positive.");
        }

        LlmReasoningEffort.Validate(request.ReasoningLevel, capabilities);
        if (request.MaxOutputTokens is not null
            && capabilities.OutputLimitEnforcement == Common.LLM.LlmOutputLimitEnforcement.Unsupported)
        {
            throw new NotSupportedException("This route cannot enforce MaxOutputTokens; an output reserve is budgeting only.");
        }

        if (request.StructuredOutput is { } structured
            && (string.IsNullOrWhiteSpace(structured.Name)
                || structured.Schema.ValueKind != JsonValueKind.Object))
        {
            throw new ArgumentException(
                "Structured output requires a name and a JSON object schema.",
                nameof(request));
        }

        ValidatePromptAttachments(request, capabilities);
        ValidateToolUse(request, capabilities);
    }

    internal static void ValidatePromptAttachments(
        LlmChatRequest request,
        LlmProviderCapabilities capabilities)
    {
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
                nameof(request));
        }
    }

    internal static void ValidateToolUse(
        LlmChatRequest request,
        LlmProviderCapabilities capabilities)
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

        var toolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (LlmToolDefinition tool in request.Tools)
        {
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

        if (request.ToolChoice is { Mode: LlmToolChoiceMode.Specific } choice
            && (string.IsNullOrWhiteSpace(choice.Name) || !toolNames.Contains(choice.Name)))
        {
            throw new ArgumentException(
                "A specific tool choice must name one of the request tools.",
                nameof(request));
        }

        if (request.ToolChoice is { Mode: not LlmToolChoiceMode.None }
            && request.Tools.Count == 0)
        {
            throw new ArgumentException("Tool choice requires at least one request tool.", nameof(request));
        }

        if (request.AllowParallelToolCalls is not null && request.Tools.Count == 0)
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

            foreach (LlmToolCall call in message.ToolCalls)
            {
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

            foreach (LlmToolResult result in message.ToolResults)
            {
                if (string.IsNullOrWhiteSpace(result.ToolCallId))
                {
                    throw new ArgumentException(
                        "Tool results require a tool-call id.",
                        nameof(request));
                }

                LlmAttachments.Validate(
                    result.Attachments,
                    capabilities.SupportsToolResultImages,
                    capabilities.SupportsToolResultDocuments,
                    nameof(request));
            }
        }
    }

    protected LlmProviderException ProtocolException(
        string message,
        HttpResponseMessage response,
        string rawBody,
        LlmCredential? credential,
        Exception? innerException = null) => new(
            message,
            Transport,
            response.StatusCode,
            requestId: ProviderHttp.ReadRequestId(response),
            sanitizedResponseBody: SecretRedactor.Sanitize(rawBody, credential),
            innerException: innerException);

    private void EnsureTransport(LlmConnection connection)
    {
        if (connection.Transport != Transport)
        {
            throw new ArgumentException(
                $"A {connection.Transport} connection cannot be used with the {Transport} client.",
                nameof(connection));
        }
    }
}
