using Darkspyre.Inference.Common;
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

    protected virtual bool OffersUsageDeltas => DefaultCapabilities.SupportsUsageDeltas == InferenceSupport.Supported;

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
        if ((capabilities.SupportsModelListing == InferenceSupport.Unsupported))
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

    /// <summary>
    /// Checks the operations a request invokes. Values the model cannot use were already mapped or dropped by
    /// <see cref="LlmRequestNormalizer"/>.
    /// </summary>
    protected void ValidateRequest(
        LlmConnection connection,
        LlmChatRequest request,
        LlmProviderCapabilities capabilities)
    {
        EnsureTransport(connection);
        ExplicitCaches.Validate(request, (capabilities.SupportsExplicitCache != InferenceSupport.Unsupported));
        HostedTools.Validate(request, capabilities);
        ClientTools.Validate(request, (capabilities.SupportsShellTools != InferenceSupport.Unsupported));
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            throw new ArgumentException("A model id is required.", nameof(request));
        }

        ValidateToolUse(request, capabilities);
        ValidateAdditionalRequest(connection, request);
    }

    protected virtual bool SupportsResourcePreparation => false;
    /// <summary>Reports the adapter's implemented logical-resource preparation lane.</summary>
    public bool CanPrepareResources => SupportsResourcePreparation;

    protected virtual void ValidateAdditionalRequest(LlmConnection connection, LlmChatRequest request) { }

    /// <summary>Refuses tool use on a model that marks it Unsupported.</summary>
    internal static void ValidateToolUse(LlmChatRequest request, LlmProviderCapabilities capabilities)
    {
        bool hasToolHistory = request.Messages.Any(message =>
            message.ToolCalls.Count > 0 || message.ToolResults.Count > 0);
        // A text transcript carries tool history without asking the provider to run host tools.
        bool requestsToolUse = request.Tools.Count > 0
            || request.ToolChoice is not null
            || request.AllowParallelToolCalls is not null
            || (hasToolHistory && capabilities.ToolHistory == LlmToolHistory.Structured);

        if (requestsToolUse && (capabilities.SupportsToolUse == InferenceSupport.Unsupported))
        {
            throw new NotSupportedException("The selected connection does not support host-executed tools.");
        }
    }

    /// <summary>
    /// Projects a tool output with the connection's support as the route and the model's support as the target.
    /// </summary>
    internal ProjectedToolOutput ProjectToolOutput(LlmConnection connection, string modelId, LlmToolOutput output) =>
        ToolOutputProjector.Project(
            output,
            GetCapabilities(connection).RichContentSupported,
            GetCapabilities(connection, modelId).RichContentSupported);

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
