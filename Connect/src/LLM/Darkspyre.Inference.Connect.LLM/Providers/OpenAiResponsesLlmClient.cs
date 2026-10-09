using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using System.Collections.Frozen;
using LlmReasoningLevel = Darkspyre.Inference.Common.LLM.LlmReasoningLevel;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM.Providers;

/// <summary>Streaming adapter for OpenAI's Responses API.</summary>
internal sealed class OpenAiResponsesLlmClient(HttpClient http) : ResponsesLlmClientBase(http)
{
    public override LlmTransport Transport => LlmTransport.OpenAiResponses;

    public override LlmProviderDefinition ProviderDefinition => ProviderDefinitions.OpenAiApiKey;

    protected override bool OffersNonStreamingRequests => true;
    protected override bool SupportsResourcePreparation => true;

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = ProtocolCapabilities with
    {
        SupportsAgentSessions = InferenceSupport.Supported,
        SupportsAgentToolUse = InferenceSupport.Supported,
        SupportsAgentTurnInstructions = InferenceSupport.Supported,
        Agent = ManagedAgentPolicy.Capabilities with { RichContentSupported = ProtocolCapabilities.RichContentSupported }
    };

    internal static LlmProviderCapabilities ProtocolCapabilities { get; } = new()
    {
        OutputLimitEnforcement = Common.LLM.LlmOutputLimitEnforcement.ProviderEnforced,
        SupportsStreaming = InferenceSupport.Supported,
        SupportsReasoningText = InferenceSupport.Supported,
        SupportsStreamingUsage = InferenceSupport.Supported,
        SupportsModelListing = InferenceSupport.Supported,
        SupportsJsonSchema = InferenceSupport.Supported,
        SupportsJsonObject = InferenceSupport.Supported,
        // Reasoning levels and temperature are per model; GetCapabilities sets them from the model's profile.
        ReasoningLevels = LlmReasoningEffort.Levels(),
        SupportsTemperature = InferenceSupport.Unsupported,
        SupportsToolUse = InferenceSupport.Supported,
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.None, LlmToolChoiceMode.Required, LlmToolChoiceMode.Specific }.ToFrozenSet(),
            StrictInputSchema = LlmStrictInputSchemaSupport.Supported,
            SupportsParallelCalls = InferenceSupport.Supported,
            SupportsParallelCallControl = InferenceSupport.Supported,
            SupportsArgumentStreaming = InferenceSupport.Supported
        },
        SupportsPromptImages = InferenceSupport.Supported,
        SupportsPromptDocuments = InferenceSupport.Supported,
        RichContentSupported = RichContentRoutes.Responses
    };

    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        LlmProviderCapabilities capabilities = base.GetCapabilities(connection, modelId);
        OpenAiModelProfile profile = OpenAiModelProfiles.Get(modelId);
        return capabilities with
        {
            PromptCache = (profile.SupportsPromptCacheOptions != InferenceSupport.Unsupported) ? PromptCaching.OpenAi : PromptCaching.Automatic,
            ReasoningLevels = LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.ReasoningLevels)
                ? capabilities.ReasoningLevels
                : profile.ReasoningLevels,
            SupportsTemperature = LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.SupportsTemperature)
                ? capabilities.SupportsTemperature
                : (profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsTopP = profile.AllowsSampling
                ? capabilities.SupportsTopP : InferenceSupport.Unsupported,
            SupportsTopK = InferenceSupport.Unsupported,
            SupportsSeed = InferenceSupport.Unsupported,
            SupportsPresencePenalty = InferenceSupport.Unsupported,
            SupportsFrequencyPenalty = InferenceSupport.Unsupported,
            MaxStopSequences = 0
        };
    }
}
