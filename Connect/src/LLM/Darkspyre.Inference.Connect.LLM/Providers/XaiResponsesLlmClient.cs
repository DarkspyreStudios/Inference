using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>OAuth-only Grok Responses adapter. The facade owns its published chat catalog.</summary>
internal sealed class XaiResponsesLlmClient(HttpClient http) : ResponsesLlmClientBase(http), ILlmCatalogCapabilities
{
    private const int MaxImageBytes = 20 * 1024 * 1024;
    private static readonly FrozenSet<string> ReservedOptions = new[]
    {
        "model", "input", "stream", "store", "include", "previous_response_id", "service_tier", "instructions",
        "temperature", "top_p", "top_k", "seed", "presence_penalty", "frequency_penalty", "stop", "stop_sequences",
        "max_output_tokens", "reasoning", "reasoning_effort", "tools", "tool_choice", "parallel_tool_calls", "text",
        "prompt_cache_key", "background", "deferred"
    }.ToFrozenSet(StringComparer.Ordinal);

    public override LlmTransport Transport => LlmTransport.XaiResponses;
    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.GrokSubscription;
    protected override bool OffersNonStreamingRequests => true;
    protected override bool OffersUsageDeltas => false;

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        OutputLimitEnforcement = LlmOutputLimitEnforcement.ProviderEnforced,
        SupportsStreaming = true,
        SupportsStreamingUsage = true,
        SupportsReasoningText = true,
        SupportsModelListing = true,
        SupportsJsonSchema = true,
        SupportsJsonObject = true,
        SupportsToolUse = true,
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.None, LlmToolChoiceMode.Required, LlmToolChoiceMode.Specific }.ToFrozenSet(),
            StrictInputSchema = LlmStrictInputSchemaSupport.Supported,
            SupportsParallelCalls = true,
            SupportsParallelCallControl = true,
            SupportsArgumentStreaming = true
        },
        SupportsPromptDocuments = true,
        RichContentSupported = RichContentRoutes.Responses
    };

    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        ValidateEndpoint(connection);
        XaiModelProfile profile = XaiModelProfiles.Get(connection, modelId);
        LlmProviderCapabilities defaults = DefaultCapabilities with
        {
            ReasoningLevels = profile.ReasoningLevels,
            SupportsTemperature = profile.AllowsSampling,
            SupportsTopP = profile.AllowsSampling,
            SupportsPromptImages = profile.AcceptsImages,
            RichContentSupported = DefaultCapabilities.RichContentSupported with
            {
                Images = modelId is null || profile.AcceptsImages ? LlmRichContentCarriage.Native : LlmRichContentCarriage.Unsupported
            }
        };
        LlmProviderCapabilities capabilities = LlmCapabilityOverrideLookup.Apply(defaults, connection, modelId, OffersUsageDeltas);
        return capabilities with
        {
            ReasoningLevels = profile.MultiAgent is null ? capabilities.ReasoningLevels.Where(level => level is
                LlmReasoningLevel.Low or LlmReasoningLevel.Medium or LlmReasoningLevel.High or LlmReasoningLevel.ExtraHigh).ToFrozenSet()
                : LlmReasoningEffort.Levels(),
            SupportsPromptImages = capabilities.SupportsPromptImages && profile.AcceptsImages,
            SupportsTopK = false,
            SupportsSeed = false,
            SupportsPresencePenalty = false,
            SupportsFrequencyPenalty = false,
            // The Responses request schema publishes no string-stop field.
            MaxStopSequences = 0
        };
    }

    protected override JsonObject BuildPayload(LlmConnection connection, LlmChatRequest request,
        LlmStructuredOutputEnforcement? enforcement, bool stream)
    {
        _ = Authorization(connection);
        if (connection.ProviderOptions.Keys.Concat(request.ProviderOptions.Keys).Any(ReservedOptions.Contains))
            throw new ArgumentException("Grok protocol controls use typed request and connection properties, not provider options.", nameof(request));
        if (request.Temperature is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(request), "Grok temperature must be in [0, 2].");
        if (request.Tools.Count > 350)
            throw new NotSupportedException("Grok accepts at most 350 client tools.");
        if (request.Tools.Any(tool => tool.Name == "web_search"))
            throw new ArgumentException("A Grok client function cannot be named web_search.", nameof(request));
        foreach (LlmAttachment attachment in request.Messages.SelectMany(message => message.Attachments))
            if (attachment.Kind == LlmBinaryKind.Image && attachment.Data.Length > MaxImageBytes)
                throw new ArgumentException("Grok input images must not exceed 20 MiB.", nameof(request));
        if (GetCapabilities(connection, request.ModelId).RichContentSupported.Images == LlmRichContentCarriage.Native)
            foreach (LlmToolOutputPart part in request.Messages.SelectMany(message => message.ToolResults)
                .SelectMany(result => result.Output.Parts))
                if (part is LlmToolOutputBinary { Kind: LlmBinaryKind.Image } binary && binary.Data.Length > MaxImageBytes
                    || part is LlmToolOutputResource { BodyKind: LlmBinaryKind.Image, Data: { } data } && data.Length > MaxImageBytes)
                    throw new ArgumentException("Grok tool input images must not exceed 20 MiB.", nameof(request));
        foreach (LlmToolDefinition tool in request.Tools.Where(tool => tool.Strict))
            if (!XaiSchemaLimits.Fits(tool.InputSchema))
                throw new NotSupportedException("A strict Grok tool schema exceeds the published numeric guarantees. The schema is not weakened or sent as non-strict.");

        XaiModelProfile profile = XaiModelProfiles.Get(connection, request.ModelId);
        if (request.ReasoningLevel is null && profile.DefaultReasoningLevel is { } defaultLevel
            && GetCapabilities(connection, request.ModelId).ReasoningLevels.Contains(defaultLevel))
            request = request with { ReasoningLevel = defaultLevel };
        JsonObject payload = base.BuildPayload(connection, request, enforcement, stream);
        payload["service_tier"] = (connection.Xai?.ServiceTier ?? XaiServiceTier.Default) == XaiServiceTier.Priority ? "priority" : "default";
        return payload;
    }

    protected override bool ReasonsByDefault(LlmConnection connection, string modelId) =>
        XaiModelProfiles.Get(connection, modelId).DefaultReasoningLevel is not null;

    protected override LlmStructuredOutputEnforcement? ResolveStructuredOutputEnforcement(
        LlmConnection connection, LlmChatRequest request, LlmProviderCapabilities capabilities) =>
        request.StructuredOutput is { } output ? StructuredOutput.ResolveEnforcement(output,
            XaiSchemaLimits.Fits(output.Schema) ? capabilities : capabilities with { SupportsJsonSchema = false, SupportsJsonObject = false }) : null;

    protected override void ApplyRequestHeaders(HttpRequestMessage message, LlmConnection connection, LlmChatRequest request) =>
        ApplyHeaders(message, connection, message.Method == HttpMethod.Post && GetCapabilities(connection, request.ModelId).SupportsStreaming);

    public override async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection, CancellationToken cancellationToken = default)
    {
        if (!GetCapabilities(connection).SupportsModelListing) return [];
        JsonArray generic = await ListingAsync(connection, "models", "data", cancellationToken);
        JsonArray language = await ListingAsync(connection, "language-models", "models", cancellationToken);
        var results = new List<LlmModel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonObject item in language.OfType<JsonObject>())
        {
            string? id = String(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!ids.Add(id)) throw new LlmProviderException("Grok model discovery returned duplicate model identifiers.", Transport);
            JsonObject metadata = (JsonObject)item.DeepClone();
            JsonObject? match = generic.OfType<JsonObject>().FirstOrDefault(candidate => String(candidate, "id") == id)
                ?? generic.OfType<JsonObject>().FirstOrDefault(candidate => Aliases(candidate).Contains(id, StringComparer.Ordinal)
                    || String(candidate, "id") is { } genericId && Aliases(item).Contains(genericId, StringComparer.Ordinal));
            if (match is not null)
            {
                foreach ((string key, JsonNode? value) in match)
                    if (!metadata.ContainsKey(key)) metadata[key] = value?.DeepClone();
                if (metadata["capabilities"] is JsonObject capabilities && match["capabilities"] is JsonObject genericCapabilities)
                    foreach ((string key, JsonNode? value) in genericCapabilities)
                        if (!capabilities.ContainsKey(key)) capabilities[key] = value?.DeepClone();
                string[] aliases = [.. Aliases(item).Concat(Aliases(match)).Distinct(StringComparer.Ordinal)];
                if (aliases.Length > 0) metadata["aliases"] = JsonSerializer.SerializeToNode(aliases);
            }
            JsonElement entry = JsonSerializer.SerializeToElement(metadata);
            results.Add(new LlmModel(id, String(item, "name") ?? id, String(item, "owned_by"))
            {
                CatalogEntry = entry,
                MaxContextTokens = entry.TryGetProperty("context_length", out JsonElement length) && length.ValueKind == JsonValueKind.Number
                    && length.TryGetInt64(out long count) && count > 0 ? count : null
            });
        }
        LlmConnection published = connection with { Catalog = results };
        return [.. results.Select(model => model with { Capabilities = GetCapabilities(published, model.Id) })
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<JsonArray> ListingAsync(LlmConnection connection, string path, string array, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProviderHttp.BuildUri(connection, path));
        ApplyHeaders(request, connection, false);
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        await XaiErrorClassifier.ThrowIfUnsuccessfulAsync(response, LlmCredential.Parse(connection.Credential), [], cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root[array] is JsonArray models
                && models.All(item => item is JsonObject)) return (JsonArray)models.DeepClone();
        }
        catch (JsonException) { }
        throw ProtocolException("Grok model discovery returned an invalid catalog.", response, body, LlmCredential.Parse(connection.Credential));
    }

    private static string? String(JsonObject item, string name) => item[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static IEnumerable<string> Aliases(JsonObject item) => item["aliases"] is JsonArray aliases
        ? aliases.OfType<JsonValue>().Select(value => value.TryGetValue(out string? text) ? text : null).OfType<string>() : [];

    private static void ApplyHeaders(HttpRequestMessage message, LlmConnection connection, bool stream)
    {
        AuthenticationHeaderValue authorization = Authorization(connection);
        ProviderHttp.ApplyHeaders(message, connection, applyApiKeyAsBearer: false);
        message.Headers.Authorization = authorization;
        message.Headers.Accept.Clear();
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
    }

    private static AuthenticationHeaderValue Authorization(LlmConnection connection)
    {
        ValidateEndpoint(connection);
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        if (credential is null || !string.IsNullOrWhiteSpace(credential.ApiKey) || credential.Aws is not null || credential.Google is not null
            || credential.OAuth?.AccessToken is not { Length: > 0 } token || token.Any(char.IsWhiteSpace)
            || !AuthenticationHeaderValue.TryParse("Bearer " + token, out AuthenticationHeaderValue? authorization)
            || authorization.Scheme != "Bearer" || authorization.Parameter != token)
            throw new InferenceException(InferenceFailureCode.AuthenticationRequired, "Grok requires a valid OAuth credential.");
        return authorization;
    }

    private static void ValidateEndpoint(LlmConnection connection)
    {
        if (connection.Transport != LlmTransport.XaiResponses) throw new ArgumentException("Select the Grok Responses transport.", nameof(connection));
        XaiConnectionOptions options = connection.Xai ?? new();
        if (!Enum.IsDefined(options.ServiceTier)) throw new ArgumentOutOfRangeException(nameof(connection), "Select an xAI service tier.");
        if (!string.Equals(connection.Endpoint.TrimEnd('/'), options.GetEndpoint(), StringComparison.Ordinal))
            throw new ArgumentException("The Grok endpoint must match its typed region.", nameof(connection));
    }
}
