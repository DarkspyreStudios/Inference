using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using System.Collections.Frozen;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>OpenAI API transport, and any service at another endpoint that serves the same API.</summary>
internal sealed class OpenAiResponsesLlmClient(HttpClient http)
    : ResponsesLlmClientBase(http), ILlmCatalogCapabilities, IExternalLlmAuthenticationProbe
{
    internal const string DefaultEndpoint = "https://api.openai.com/v1";

    public override LlmTransport Transport => LlmTransport.OpenAiResponses;

    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;

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

    /// <summary>The feature list for the connection's endpoint and the model's profile (B11, B12).</summary>
    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        EnsureTransport(connection);
        LlmProviderCapabilities table = DefaultCapabilities;
        if (IsOpenAiEndpoint(connection))
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
                    ? PromptCaching.OpenAi : PromptCaching.Automatic
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

    /// <summary>Always true: the key is optional (B1). A missing or wrong key returns the service's real error.</summary>
    public Task<bool> IsAuthenticatedAsync(LlmConnection connection, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    /// <summary>Only a verified reasoning profile on OpenAI's endpoint reasons by default. Elsewhere a request with no
    /// level gets no reasoning object, so a cleared level after a reasoning refusal sends none (5.10).</summary>
    protected override bool ReasonsByDefault(LlmConnection connection, string modelId) =>
        IsOpenAiEndpoint(connection) && OpenAiModelProfiles.Get(modelId) is { Verified: true, IsReasoning: true };

    /// <summary>True when the endpoint's host is api.openai.com.</summary>
    internal static bool IsOpenAiEndpoint(LlmConnection connection) =>
        Uri.TryCreate(connection.Endpoint, UriKind.Absolute, out Uri? endpoint)
        && string.Equals(endpoint.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase);
}
