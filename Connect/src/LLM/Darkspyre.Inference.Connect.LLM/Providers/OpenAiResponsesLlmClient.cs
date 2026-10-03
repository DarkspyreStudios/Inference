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

    protected override LlmProviderCapabilities DefaultCapabilities { get; } = ProtocolCapabilities with
    {
        SupportsAgentSessions = true,
        SupportsAgentToolUse = true,
        SupportsAgentTurnInstructions = true,
        Agent = ManagedAgentPolicy.Capabilities with { RichContentSupported = ProtocolCapabilities.RichContentSupported }
    };

    internal static LlmProviderCapabilities ProtocolCapabilities { get; } = new()
    {
        OutputLimitEnforcement = Common.LLM.LlmOutputLimitEnforcement.ProviderEnforced,
        SupportsStreaming = true,
        SupportsReasoningText = true,
        SupportsStreamingUsage = true,
        SupportsModelListing = true,
        SupportsJsonSchema = true,
        SupportsJsonObject = true,
        // Reasoning levels and temperature are per model; GetCapabilities sets them from the model's profile.
        ReasoningLevels = LlmReasoningEffort.Levels(),
        SupportsTemperature = false,
        SupportsToolUse = true,
        Tools = new()
        {
            ChoiceModes = new[] { LlmToolChoiceMode.Auto, LlmToolChoiceMode.None, LlmToolChoiceMode.Required, LlmToolChoiceMode.Specific }.ToFrozenSet(),
            StrictInputSchema = LlmStrictInputSchemaSupport.Supported,
            SupportsParallelCalls = true,
            SupportsParallelCallControl = true,
            SupportsArgumentStreaming = true
        },
        SupportsPromptImages = true,
        SupportsPromptDocuments = true,
        RichContentSupported = RichContentRoutes.Responses
    };

    public override LlmProviderCapabilities GetCapabilities(LlmConnection connection, string? modelId = null)
    {
        LlmProviderCapabilities capabilities = base.GetCapabilities(connection, modelId);
        OpenAiModelProfile profile = OpenAiModelProfiles.Get(modelId);
        return capabilities with
        {
            ReasoningLevels = LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.ReasoningLevels)
                ? capabilities.ReasoningLevels
                : profile.ReasoningLevels,
            SupportsTemperature = LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.SupportsTemperature)
                ? capabilities.SupportsTemperature
                : profile.AllowsSampling,
            SupportsTopP = profile.AllowsSampling
                && (!LlmCapabilityOverrideLookup.Overrides(connection, modelId, overrides => overrides.SupportsTopP)
                    || capabilities.SupportsTopP),
            SupportsTopK = false,
            SupportsSeed = false,
            SupportsPresencePenalty = false,
            SupportsFrequencyPenalty = false,
            MaxStopSequences = 0
        };
    }
}
