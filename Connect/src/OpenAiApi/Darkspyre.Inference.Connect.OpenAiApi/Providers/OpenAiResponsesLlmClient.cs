using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>OpenAI API transport, and any service at another endpoint that serves the same API.</summary>
internal sealed class OpenAiResponsesLlmClient(HttpClient http)
    : ResponsesLlmClientBase(http), ILlmCatalogCapabilities, IExternalLlmAuthenticationProbe, ILlmTextTokenizerClient
{
    internal const string DefaultEndpoint = "https://api.openai.com/v1";

    public override LlmTransport Transport => LlmTransport.OpenAiResponses;

    public override LlmProviderDefinition ProviderDefinition => OpenAiApiDefinition.Value;

    protected override bool SupportsResourcePreparation => true;

    /// <summary>The feature list of an endpoint other than OpenAI's: every service feature not confirmed there is Unknown (B12).</summary>
    protected override LlmProviderCapabilities DefaultCapabilities { get; } = new()
    {
        OutputLimitEnforcement = LlmOutputLimitEnforcement.Unknown,
        SupportsTopK = InferenceSupport.Unsupported,
        SupportsSeed = InferenceSupport.Unsupported,
        SupportsPresencePenalty = InferenceSupport.Unsupported,
        SupportsFrequencyPenalty = InferenceSupport.Unsupported,
        MaxStopSequences = 0,
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.None, LlmToolChoiceMode.Required, LlmToolChoiceMode.Specific }.ToFrozenSet()
        },
        // The service attempts the image tool; the local server ignores it without error (P1-B2 Unknown).
        HostedTools = new[] { LlmHostedToolKind.ImageGeneration }.ToFrozenSet(),
        SupportsAccountUsage = InferenceSupport.Unsupported,
        SupportsAgentSessions = InferenceSupport.Supported,
        SupportsAgentToolUse = InferenceSupport.Supported,
        SupportsAgentTurnInstructions = InferenceSupport.Supported,
        Agent = ManagedAbilities with { RichContentSupported = RichContentRoutes.Responses },
        RichContentSupported = RichContentRoutes.Responses
    };

    private static readonly IReadOnlySet<LlmServiceTier> AllServiceTiers = Enum.GetValues<LlmServiceTier>().ToFrozenSet();

    /// <summary>The feature list for the connection's endpoint and the model's profile (B11, B12).</summary>
    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        EnsureTransport(connection);
        bool openAi = IsOpenAiEndpoint(connection);
        // The connection's Service tiers switch; unset, it is on for OpenAI's endpoint only (B13, S11 step 4).
        bool tiers = (connection.CapabilityOverrides?.SupportsServiceTiers
            ?? (openAi ? InferenceSupport.Supported : InferenceSupport.Unsupported)) == InferenceSupport.Supported;
        LlmProviderCapabilities table = DefaultCapabilities with { ServiceTiers = tiers ? AllServiceTiers : FrozenSet<LlmServiceTier>.Empty };
        if (openAi)
        {
            OpenAiModelProfile profile = OpenAiModelProfiles.Get(modelId);
            InferenceSupport sampling = !profile.Verified ? InferenceSupport.Unknown
                : profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported;
            table = table with
            {
                OutputLimitEnforcement = LlmOutputLimitEnforcement.ProviderEnforced,
                SupportsStreaming = InferenceSupport.Supported,
                SupportsStreamingUsage = InferenceSupport.Supported,
                SupportsReasoningText = InferenceSupport.Supported,
                SupportsModelListing = InferenceSupport.Supported,
                SupportsJsonSchema = InferenceSupport.Supported,
                SupportsJsonObject = InferenceSupport.Supported,
                SupportsToolUse = InferenceSupport.Supported,
                SupportsPromptImages = InferenceSupport.Supported,
                SupportsPromptDocuments = InferenceSupport.Supported,
                Tools = table.Tools with
                {
                    SupportsParallelCalls = InferenceSupport.Supported,
                    SupportsParallelCallControl = InferenceSupport.Supported,
                    SupportsArgumentStreaming = InferenceSupport.Supported
                },
                ReasoningLevels = profile.ReasoningLevels,
                SupportsTemperature = sampling,
                SupportsTopP = sampling,
                SamplingOnlyWithoutReasoning = profile.SamplingRequiresNoReasoning,
                PromptCache = profile.SupportsPromptCacheOptions == InferenceSupport.Supported
                    ? PromptCaching.OpenAi : PromptCaching.Automatic,
                ServiceTiers = tiers ? profile.ServiceTiers ?? FrozenSet<LlmServiceTier>.Empty : FrozenSet<LlmServiceTier>.Empty
            };
        }
        return LlmCapabilityOverrideLookup.Apply(table, connection, modelId, OffersUsageDeltas);
    }

    /// <summary>The service's listing; on OpenAI's endpoint a model without a window takes its published limits (B6).</summary>
    public override async Task<IReadOnlyList<LlmModel>> ListModelsAsync(LlmConnection connection,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LlmModel> listed = await base.ListModelsAsync(connection, cancellationToken).ConfigureAwait(false);
        bool openAi = IsOpenAiEndpoint(connection);
        return [.. listed.Select(model =>
        {
            if (openAi && model.MaxContextTokens is null && OpenAiModelProfiles.Get(model.Id) is { ContextWindow: { } window } profile)
                model = model with { MaxContextTokens = window, MaxOutputTokens = profile.MaxOutputTokens };
            // A context length set on the connection is the model's limit (5.11); this is the one place it is applied.
            return LlmCapabilityOverrideLookup.ContextLength(connection, model.Id) is { } length
                ? model with { MaxContextTokens = length } : model;
        })];
    }

    /// <summary>Tokenizes exact plain text through the explicitly configured native llama.cpp endpoint.</summary>
    public async Task<LlmTokenization> TokenizeAsync(LlmConnection connection, string modelId, string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(text);
        EnsureTransport(connection);
        Uri endpoint = connection.LlamaCppTokenizationEndpoint
            ?? throw new NotSupportedException("This route does not implement plain-text tokenization.");
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("The llama.cpp tokenization endpoint must be an absolute HTTP or HTTPS URL.");

        using var consumption = SdkHttpConsumption.Enter(Http, cancellationToken);
        cancellationToken = consumption.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        ProviderHttp.ApplyHeaders(request, connection);
        // Text and model identity are opaque data. Changing either changes the provider's tokens.
        var payload = new JsonObject
        {
            ["model"] = modelId, ["content"] = text,
            ["add_special"] = false, ["parse_special"] = false, ["with_pieces"] = true
        };
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        LlmCredential? credential = LlmCredential.Parse(connection.Credential);
        string[] Secrets() => request.Headers.SelectMany(header => header.Value)
            .Concat(request.Headers.Authorization?.Parameter is { } bearer ? [bearer] : []).ToArray();
        HttpResponseMessage sent;
        try
        {
            sent = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new LlmProviderException(SecretRedactor.Sanitize(error.Message, credential, Secrets()), Transport,
                (error as HttpRequestException)?.StatusCode);
        }
        using HttpResponseMessage response = sent;
        cancellationToken.ThrowIfCancellationRequested();
        string[] secrets = Secrets();
        await ProviderHttp.ThrowIfUnsuccessfulAsync(response, Transport, credential, "llama.cpp tokenization",
            secrets, cancellationToken).ConfigureAwait(false);
        string body = await SdkHttpConsumption.ReadAsStringAsync(response.Content, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        LlmTokenization result;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tokens", out JsonElement entries)
                || entries.ValueKind != JsonValueKind.Array)
                throw InvalidTokens();
            var tokens = new List<LlmToken>(entries.GetArrayLength());
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out JsonElement id) || !id.TryGetUInt32(out uint number)
                    || !entry.TryGetProperty("piece", out JsonElement piece))
                    throw InvalidTokens();
                string? display;
                byte[] bytes;
                if (piece.ValueKind == JsonValueKind.String)
                {
                    display = piece.GetString()!;
                    bytes = Encoding.UTF8.GetBytes(display);
                }
                else if (piece.ValueKind == JsonValueKind.Array)
                {
                    display = null;
                    bytes = new byte[piece.GetArrayLength()];
                    int index = 0;
                    foreach (JsonElement item in piece.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!item.TryGetByte(out byte value)) throw InvalidTokens();
                        bytes[index++] = value;
                    }
                }
                else throw InvalidTokens();
                tokens.Add(new(number, display, bytes));
            }
            result = new LlmTokenization(tokens.ToArray());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            throw InvalidTokens();
        }
        return consumption.Publish(result);

        LlmProviderException InvalidTokens() => ProtocolException(
            "llama.cpp tokenization returned an invalid token sequence.", response, string.Empty, credential,
            extraSecrets: secrets);
    }

    /// <summary>Always signed in: the key is optional (B1). A missing or wrong key returns the service's real error.</summary>
    public Task<ExternalAuthenticationState> CheckAuthenticationAsync(LlmConnection connection, CancellationToken cancellationToken = default) =>
        Task.FromResult(ExternalAuthenticationState.SignedIn);

    /// <summary>Only a verified reasoning profile on OpenAI's endpoint reasons by default. Elsewhere a request with no
    /// level gets no reasoning object, so a cleared level after a reasoning refusal sends none (5.10).</summary>
    protected override bool ReasonsByDefault(LlmConnection connection, string modelId) =>
        IsOpenAiEndpoint(connection) && OpenAiModelProfiles.Get(modelId) is { Verified: true, IsReasoning: true };

}
