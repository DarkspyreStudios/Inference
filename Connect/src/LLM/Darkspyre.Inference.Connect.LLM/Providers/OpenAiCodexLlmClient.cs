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

    protected override LlmAccountUsageSnapshot? ObserveAccountUsage(HttpResponseMessage response) =>
        CodexRateLimitHeaders.Parse(response.Headers, DateTimeOffset.UtcNow);

    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.ChatGptSubscription;

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        OutputLimitEnforcement = Common.LLM.LlmOutputLimitEnforcement.Unsupported,
        SupportsStreaming = true,
        SupportsReasoningText = true,
        SupportsStreamingUsage = true,
        SupportsModelListing = true,
        SupportsAccountUsage = true,
        SupportsJsonSchema = true,
        // A model whose catalog entry lists its reasoning levels offers exactly those; see GetCapabilities.
        ReasoningLevels = LlmReasoningEffort.Levels(
            LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium,
            LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh),
        SupportsToolUse = true,
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.None, LlmToolChoiceMode.Required, LlmToolChoiceMode.Specific }.ToFrozenSet(),
            StrictInputSchema = LlmStrictInputSchemaSupport.Supported,
            SupportsParallelCalls = true,
            SupportsParallelCallControl = true,
            SupportsArgumentStreaming = true
        },
        SupportsAgentSessions = true,
        SupportsAgentToolUse = true,
        SupportsAgentTurnInstructions = true,
        Agent = ManagedAgentPolicy.Capabilities,
        SupportsPromptImages = true,
        SupportsPromptDocuments = true,
        RichContentSupported = RichContentRoutes.Responses
    };

    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        ValidateConnection(connection);
        LlmProviderCapabilities capabilities = base.GetCapabilities(connection, modelId);
        return capabilities with
        {
            SupportsTemperature = false,
            SupportsTopP = false,
            SupportsTopK = false,
            SupportsSeed = false,
            SupportsPresencePenalty = false,
            SupportsFrequencyPenalty = false,
            MaxStopSequences = 0,
            SupportsJsonObject = false,
            ReasoningLevels = !LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.ReasoningLevels)
                && CatalogLevels(ILlmCatalogCapabilities.EntryFor(connection, modelId)) is { } catalog
                    ? catalog
                    : capabilities.ReasoningLevels
        };
    }

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
        LlmConnection connection, LlmChatRequest request, LlmStructuredOutputEnforcement? enforcement, bool stream)
    {
        if (request.MaxOutputTokens is not null)
        {
            throw new NotSupportedException("Codex subscription does not accept MaxOutputTokens. Use the managed context output reserve for budgeting.");
        }
        var instructions = new List<string>();
        if (request.Instructions is not null)
        {
            instructions.Add(request.Instructions);
        }
        foreach (var message in request.Messages.Where(message => message.Role is "system" or "developer"))
        {
            if (message.ContinuationState is not null || message.ToolCalls.Count > 0 || message.ToolResults.Count > 0)
            {
                throw new ArgumentException("Assistant state and tool exchanges cannot be used as instructions.");
            }
            instructions.Add(message.Content);
        }
        LlmChatRequest projected = request with
        {
            Messages = [.. request.Messages.Where(message => message.Role is not ("system" or "developer"))]
        };
        JsonObject payload = base.BuildPayload(connection, projected, enforcement, stream);
        payload["instructions"] = string.Join("\n\n", instructions);
        return payload;
    }

    protected override bool ReasonsByDefault(LlmConnection connection, string modelId) =>
        CatalogLevels(ILlmCatalogCapabilities.EntryFor(connection, modelId)) is { } levels
            ? levels.Any(level => level > LlmReasoningLevel.None)
            : GetCapabilities(connection, modelId).SupportsReasoning;

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
        if (!GetCapabilities(connection).SupportsModelListing) { return []; }
        using var request = new HttpRequestMessage(HttpMethod.Get,
            ProviderHttp.BuildUri(connection, $"models?client_version={CodexCompatibilityVersion}"));
        ApplyCodexHeaders(request, connection);
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        using var response = await Http.SendAsync(request, cancellationToken);
        await ProviderHttp.ThrowIfUnsuccessfulAsync(response, Transport, LlmCredential.Parse(connection.Credential),
            "Codex model discovery", cancellationToken);
        using var document = JsonDocument.Parse(await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken));
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
        {
            throw new LlmProviderException("Codex model discovery did not return a model catalog.", Transport);
        }
        var result = new List<LlmModel>();
        foreach (var model in models.EnumerateArray())
        {
            string? id = LlmJson.GetString(model, "slug") ?? LlmJson.GetString(model, "id");
            if (string.IsNullOrWhiteSpace(id) || LlmJson.GetString(model, "visibility") == "hide")
            {
                continue;
            }
            if (!model.TryGetProperty("context_window", out JsonElement limit) || limit.ValueKind == JsonValueKind.Null)
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
        return consumption.Publish<IReadOnlyList<LlmModel>>(result);
    }

    public async Task<LlmAccountUsageSnapshot> GetAccountUsageAsync(
        LlmConnection connection, CancellationToken cancellationToken = default)
    {
        if (!GetCapabilities(connection).SupportsAccountUsage)
        {
            throw new NotSupportedException("The connection disables account usage.");
        }
        // Usage is a sibling of /codex, on the same configured origin; credentials never cross origins.
        var endpoint = new Uri(connection.Endpoint.TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "../wham/usage"));
        ApplyCodexHeaders(request, connection);
        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken); cancellationToken = consumption.Token;
        using var response = await Http.SendAsync(request, cancellationToken);
        await ProviderHttp.ThrowIfUnsuccessfulAsync(response, Transport, LlmCredential.Parse(connection.Credential),
            "Codex account usage", cancellationToken);
        string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
            return consumption.Publish(CodexAccountUsageParser.Parse(document.RootElement, DateTimeOffset.UtcNow));
        }
        catch (JsonException error)
        {
            throw ProtocolException("Codex account usage returned invalid JSON.", response,
            body, LlmCredential.Parse(connection.Credential), error);
        }
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
