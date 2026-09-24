using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Direct ChatGPT subscription transport; never reads CLI credentials.</summary>
internal sealed class OpenAiCodexLlmClient(HttpClient http) : ResponsesLlmClientBase(http), ILlmAccountUsageClient
{
    internal const string DefaultEndpoint = "https://chatgpt.com/backend-api/codex";
    private const string CodexCompatibilityVersion = "0.155.1";

    public override LlmTransport Transport => LlmTransport.OpenAiCodex;

    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.ChatGptSubscription;

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        OutputLimitEnforcement = Common.LLM.LlmOutputLimitEnforcement.Unsupported,
        SupportsStreaming = true,
        SupportsStreamingUsage = true,
        SupportsModelListing = true,
        SupportsAccountUsage = true,
        SupportsJsonSchema = true,
        ReasoningLevels = LlmReasoningEffort.Levels(
            LlmReasoningLevel.Minimal, LlmReasoningLevel.Low, LlmReasoningLevel.Medium,
            LlmReasoningLevel.High, LlmReasoningLevel.ExtraHigh),
        SupportsToolUse = true,
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
        return base.GetCapabilities(connection, modelId) with
        {
            SupportsTemperature = false,
            SupportsJsonObject = false
        };
    }

    protected override JsonObject BuildPayload(
        LlmConnection connection, LlmChatRequest request, LlmStructuredOutputEnforcement? enforcement)
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
            if (message.ResponseState is not null || message.ToolCalls.Count > 0 || message.ToolResults.Count > 0)
            {
                throw new ArgumentException("Assistant state and tool exchanges cannot be used as instructions.");
            }
            instructions.Add(message.Content);
        }
        LlmChatRequest projected = request with
        {
            Messages = [.. request.Messages.Where(message => message.Role is not ("system" or "developer"))]
        };
        JsonObject payload = base.BuildPayload(connection, projected, enforcement);
        payload["instructions"] = string.Join("\n\n", instructions);
        return payload;
    }

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
        using var response = await Http.SendAsync(request, cancellationToken);
        await ProviderHttp.ThrowIfUnsuccessfulAsync(response, Transport, LlmCredential.Parse(connection.Credential),
            "Codex model discovery", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
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
            result.Add(new LlmModel(id, LlmJson.GetString(model, "display_name") ?? id)
            {
                MaxContextTokens = model.TryGetProperty("context_window", out var limit)
                    && limit.ValueKind == JsonValueKind.Number && limit.TryGetInt64(out long context) && context > 0 ? context : null
            });
        }
        return result;
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
        using var response = await Http.SendAsync(request, cancellationToken);
        await ProviderHttp.ThrowIfUnsuccessfulAsync(response, Transport, LlmCredential.Parse(connection.Credential),
            "Codex account usage", cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
            return CodexAccountUsageParser.Parse(document.RootElement, DateTimeOffset.UtcNow);
        }
        catch (JsonException error)
        {
            throw ProtocolException("Codex account usage returned invalid JSON.", response,
            body, LlmCredential.Parse(connection.Credential), error);
        }
    }

    private static void ValidateConnection(LlmConnection connection)
    {
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
