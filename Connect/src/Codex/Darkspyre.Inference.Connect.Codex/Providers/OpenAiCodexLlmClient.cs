using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Direct ChatGPT subscription transport; never reads CLI credentials.</summary>
internal sealed class OpenAiCodexLlmClient(HttpClient http)
    : ResponsesLlmClientBase(http), ILlmAccountUsageClient, ILlmCatalogCapabilities
{
    internal const string DefaultEndpoint = "https://chatgpt.com/backend-api/codex";
    private const string CodexCompatibilityVersion = "0.159.3";

    public override LlmTransport Transport => LlmTransport.OpenAiCodex;

    protected override bool SupportsResourcePreparation => true;

    protected override LlmAccountUsageSnapshot? ObserveAccountUsage(HttpResponseMessage response) =>
        CodexRateLimitHeaders.Parse(response.Headers, DateTimeOffset.UtcNow);

    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.ChatGptSubscription;

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        OutputLimitEnforcement = Common.LLM.LlmOutputLimitEnforcement.Unknown,
        SupportsStreaming = InferenceSupport.Supported,
        SupportsReasoningText = InferenceSupport.Supported,
        SupportsStreamingUsage = InferenceSupport.Supported,
        SupportsModelListing = InferenceSupport.Supported,
        SupportsAccountUsage = InferenceSupport.Supported,
        PromptCache = PromptCaching.Automatic,
        SupportsJsonSchema = InferenceSupport.Supported,
        // No catalog observation means unknown effort support, not a transport-wide model allowlist.
        ReasoningLevels = LlmReasoningEffort.Levels(),
        SupportsToolUse = InferenceSupport.Supported,
        HostedTools = new[] { LlmHostedToolKind.ImageGeneration }.ToFrozenSet(),
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.None, LlmToolChoiceMode.Required, LlmToolChoiceMode.Specific }.ToFrozenSet(),
            SupportsParallelCalls = InferenceSupport.Supported,
            SupportsParallelCallControl = InferenceSupport.Supported,
            SupportsArgumentStreaming = InferenceSupport.Supported
        },
        SupportsAgentSessions = InferenceSupport.Supported,
        SupportsAgentToolUse = InferenceSupport.Supported,
        SupportsAgentTurnInstructions = InferenceSupport.Supported,
        Agent = ManagedAbilities,
        SupportsPromptImages = InferenceSupport.Supported,
        SupportsPromptDocuments = InferenceSupport.Supported,
        RichContentSupported = RichContentRoutes.Responses
    };

    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        ValidateConnection(connection);
        LlmProviderCapabilities capabilities = base.GetCapabilities(connection, modelId);
        JsonElement? entry = ILlmCatalogCapabilities.EntryFor(connection, modelId);
        bool? supportsImages = CatalogSupportsImages(entry);
        InferenceSupport? toolImages = LlmCapabilityOverrideLookup.Overrides(connection, modelId,
            overrides => overrides.SupportsToolResultImages) ? null : supportsImages switch
            {
                true => InferenceSupport.Supported,
                false => InferenceSupport.Unsupported,
                _ => InferenceSupport.Unknown
            };
        return capabilities with
        {
            SupportsTopK = InferenceSupport.Unsupported,
            SupportsSeed = InferenceSupport.Unsupported,
            SupportsPresencePenalty = InferenceSupport.Unsupported,
            SupportsFrequencyPenalty = InferenceSupport.Unsupported,
            MaxStopSequences = 0,
            SupportsJsonObject = InferenceSupport.Unsupported,
            SupportsReasoningText = !LlmCapabilityOverrideLookup.Overrides(connection, modelId,
                overrides => overrides.SupportsReasoningText)
                    ? CatalogSupportsReasoningText(entry) switch
                    {
                        true => InferenceSupport.Supported,
                        false => InferenceSupport.Unsupported,
                        _ => capabilities.SupportsReasoningText
                    }
                    : capabilities.SupportsReasoningText,
            SupportsPromptImages = !LlmCapabilityOverrideLookup.Overrides(connection, modelId,
                overrides => overrides.SupportsPromptImages)
                    ? supportsImages switch
                    {
                        true => InferenceSupport.Supported,
                        false => InferenceSupport.Unsupported,
                        _ => capabilities.SupportsPromptImages
                    }
                    : capabilities.SupportsPromptImages,
            RichContentSupported = capabilities.RichContentSupported.Restrict(toolImages, null),
            Agent = capabilities.Agent with
            {
                RichContentSupported = capabilities.Agent.RichContentSupported.Restrict(toolImages, null)
            },
            ReasoningLevels = !LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.ReasoningLevels)
                && CatalogLevels(entry) is { } catalog
                    ? catalog
                    : capabilities.ReasoningLevels
        };
    }

    private static bool? CatalogSupportsReasoningText(JsonElement? entry) =>
        entry is { ValueKind: JsonValueKind.Object } model
        && model.TryGetProperty("supports_reasoning_summary_parameter", out JsonElement supported)
        && supported.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? supported.GetBoolean()
            : null;

    private static bool? CatalogSupportsImages(JsonElement? entry) =>
        entry is { ValueKind: JsonValueKind.Object } model
        && model.TryGetProperty("input_modalities", out JsonElement modalities)
        && modalities.ValueKind == JsonValueKind.Array
        && modalities.EnumerateArray().All(modality => modality.ValueKind == JsonValueKind.String)
            ? modalities.EnumerateArray().Any(modality => modality.GetString() == "image")
            : null;

    /// <summary>
    /// The levels a Codex catalog entry lists in <c>supported_reasoning_levels</c>, or null when the entry is absent
    /// or does not list them. An effort Connect has no level for is left out.
    /// </summary>
    internal static IReadOnlySet<LlmReasoningLevel>? CatalogLevels(JsonElement? entry)
    {
        if (entry is not { } model
            || !model.TryGetProperty("supported_reasoning_levels", out JsonElement presets)
            || presets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var levels = new HashSet<LlmReasoningLevel>();
        foreach (JsonElement preset in presets.EnumerateArray())
        {
            LlmReasoningLevel? level = (preset.ValueKind == JsonValueKind.Object ? LlmJson.GetString(preset, "effort") : null) switch
            {
                "none" => LlmReasoningLevel.None,
                "minimal" => LlmReasoningLevel.Minimal,
                "low" => LlmReasoningLevel.Low,
                "medium" => LlmReasoningLevel.Medium,
                "high" => LlmReasoningLevel.High,
                "xhigh" => LlmReasoningLevel.ExtraHigh,
                "max" => LlmReasoningLevel.Maximum,
                _ => null
            };
            if (level is { } known)
            {
                levels.Add(known);
            }
        }
        return levels.ToFrozenSet();
    }

    protected override JsonObject BuildPayload(
        LlmConnection connection, LlmChatRequest request, LlmStructuredOutputEnforcement? enforcement, bool stream,
        LlmResourcePreparation? resources = null)
    {
        var instructions = new List<string>();
        if (request.Instructions is not null)
        {
            instructions.Add(request.Instructions);
        }
        // An instruction message's state and tool exchanges are dropped; only its text is sent (6.2 R43).
        foreach (var message in request.Messages.Where(message => message.Role is "system" or "developer"))
        {
            instructions.Add(message.Content);
        }
        LlmChatRequest projected = request with
        {
            Messages = [.. request.Messages.Where(message => message.Role is not ("system" or "developer"))]
        };
        if (request.StructuredOutput is { } structured && enforcement != LlmStructuredOutputEnforcement.ProviderJsonSchema)
        {
            // Without a JSON-schema format the model learns the answer's shape from the instructions.
            instructions.Add(StructuredOutput.Guidance(structured.Schema));
        }
        JsonObject payload = base.BuildPayload(connection, projected, enforcement, stream, resources);
        payload["instructions"] = string.Join("\n\n", instructions);
        return payload;
    }

    protected override bool ReasonsByDefault(LlmConnection connection, string modelId) =>
        GetCapabilities(connection, modelId).SupportsReasoning != InferenceSupport.Unsupported;

    protected override void ApplyRequestHeaders(
        HttpRequestMessage message, LlmConnection connection, LlmChatRequest request)
    {
        ApplyCodexHeaders(message, connection);
        message.Headers.Accept.Clear();
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        if (request.ConversationKey is { Length: > 0 } key)
        {
            message.Headers.Add("session-id", key);
        }
    }

    public override async Task<IReadOnlyList<LlmModel>> ListModelsAsync(
        LlmConnection connection, CancellationToken cancellationToken = default)
    {
        if ((GetCapabilities(connection).SupportsModelListing == InferenceSupport.Unsupported)) { return []; }
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        var result = new List<LlmModel>();
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                ProviderHttp.BuildUri(connection, $"models?client_version={CodexCompatibilityVersion}"));
            ApplyCodexHeaders(request, connection);
            using var response = await Http.SendAsync(request, cancellationToken);
            LlmCredential? credential = LlmCredential.Parse(connection.Credential);
            string[] secrets = request.Headers.SelectMany(header => header.Value).ToArray();
            await ProviderHttp.ThrowIfUnsuccessfulAsync(response, Transport, credential,
                "Codex model discovery", secrets, cancellationToken);
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                {
                    throw ProtocolException("Codex model discovery did not return a model catalog.", response,
                        body, credential, extraSecrets: secrets);
                }
                foreach (var model in models.EnumerateArray())
                {
                    string? id = LlmJson.GetString(model, "slug") ?? LlmJson.GetString(model, "id");
                    if (string.IsNullOrWhiteSpace(id) || LlmJson.GetString(model, "visibility") == "hide")
                    {
                        continue;
                    }
                    // A model that allows experimental context takes its catalog maximum; the live limit test confirmed
                    // Codex accepts requests that large.
                    bool experimental = model.TryGetProperty("supports_experimental_context", out JsonElement flag)
                        && flag.ValueKind == JsonValueKind.True;
                    JsonElement limit = default;
                    if ((!experimental || !model.TryGetProperty("max_context_window", out limit) || limit.ValueKind != JsonValueKind.Number)
                        && (!model.TryGetProperty("context_window", out limit) || limit.ValueKind == JsonValueKind.Null))
                    {
                        model.TryGetProperty("max_context_window", out limit);
                    }
                    result.Add(new LlmModel(id, LlmJson.GetString(model, "display_name") ?? id)
                    {
                        CatalogEntry = model.Clone(),
                        MaxContextTokens = limit.ValueKind == JsonValueKind.Number
                            && limit.TryGetInt64(out long context) && context > 0 ? context : null
                    });
                }
            }
            catch (JsonException error)
            {
                throw ProtocolException("Codex model discovery returned invalid JSON.", response,
                    body, credential, error, secrets);
            }
        }
        return consumption.Publish<IReadOnlyList<LlmModel>>(result);
    }

    public async Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(
        LlmConnection connection, CancellationToken cancellationToken = default)
    {
        if ((GetCapabilities(connection).SupportsAccountUsage == InferenceSupport.Unsupported))
        {
            throw new NotSupportedException("The connection disables account usage.");
        }
        // Usage is a sibling of /codex, on the same configured origin; credentials never cross origins.
        var endpoint = new Uri(connection.Endpoint.TrimEnd('/') + "/");
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        LlmAccountUsageSnapshot result;
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "../wham/usage"));
            ApplyCodexHeaders(request, connection);
            using var response = await Http.SendAsync(request, cancellationToken);
            LlmCredential? credential = LlmCredential.Parse(connection.Credential);
            string[] secrets = request.Headers.SelectMany(header => header.Value).ToArray();
            await ProviderHttp.ThrowIfUnsuccessfulAsync(response, Transport, credential,
                "Codex account usage", secrets, cancellationToken);
            string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
                result = CodexAccountUsageParser.Parse(document.RootElement, DateTimeOffset.UtcNow);
            }
            catch (JsonException error)
            {
                throw ProtocolException("Codex account usage returned invalid JSON.", response,
                    body, credential, error, secrets);
            }
        }
        return consumption.Publish(result);
    }

    private static void ValidateConnection(LlmConnection connection)
    {
        if (connection.AdditionalHeaders.Keys.Any(name =>
                string.Equals(name, "version", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Codex version and User-Agent headers are reserved for the selected protocol compatibility version.");
        }
        if (!Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Direct Codex requires an HTTPS endpoint, normally "
                + DefaultEndpoint + ". Replace the former CLI executable setting and sign in with ChatGPT OAuth.");
        }
        var credential = LlmCredential.Parse(connection.Credential);
        if (credential?.ApiKey is not null || credential?.Aws is not null || credential?.SecretHeaders.Count > 0)
        {
            throw new NotSupportedException("Codex subscription accepts ChatGPT OAuth only, not API-key credentials.");
        }
    }

    private static void ApplyCodexHeaders(HttpRequestMessage request, LlmConnection connection)
    {
        LlmOAuthTokens tokens = LlmCredential.Parse(connection.Credential)?.OAuth
            ?? throw new InvalidOperationException("Direct Codex requires ChatGPT OAuth sign-in through Darkspyre.Inference.Connect.LLM.");
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || string.IsNullOrWhiteSpace(tokens.AccountId))
        {
            throw new InvalidOperationException("Direct Codex requires an OAuth access token and account ID. Sign in again.");
        }
        ProviderHttp.ApplyHeaders(request, connection, applyApiKeyAsBearer: false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        request.Headers.Add("ChatGPT-Account-ID", tokens.AccountId);
        request.Headers.Add("originator", "darkspyre-inference-connect");
        request.Headers.UserAgent.ParseAdd($"Darkspyre.Inference.Connect.LLM/{CodexCompatibilityVersion}");
        request.Headers.Add("version", CodexCompatibilityVersion);
    }
}
