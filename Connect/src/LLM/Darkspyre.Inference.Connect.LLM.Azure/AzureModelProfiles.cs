using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Azure;

internal static class AzureModelProfiles
{
    internal static OpenAiModelProfile Profile(LlmConnection connection) => OpenAiModelProfiles.Get(connection.Azure!.ModelProfileId);

    internal static LlmProviderCapabilities Resolve(LlmConnection connection, string? deployment,
        LlmProviderCapabilities protocol, bool responses, bool usageDeltasImplemented)
    {
        _ = AzureEndpoints.Validate(connection, deployment, requireCredential: false);
        OpenAiModelProfile profile = Profile(connection);
        bool known = profile.Verified;
        LlmProviderCapabilities defaults = protocol with
        {
            SupportsModelListing = false,
            SupportsAccountUsage = false,
            SupportsJsonSchema = known && protocol.SupportsJsonSchema,
            SupportsJsonObject = known && protocol.SupportsJsonObject,
            SupportsPromptImages = known && protocol.SupportsPromptImages,
            SupportsPromptDocuments = known && protocol.SupportsPromptDocuments,
            ReasoningLevels = known ? profile.ReasoningLevels : LlmReasoningEffort.Levels(),
            SupportsTemperature = known && profile.AllowsSampling,
            SupportsTopP = known && profile.AllowsSampling,
            SupportsTopK = known && protocol.SupportsTopK,
            SupportsSeed = known && protocol.SupportsSeed,
            SupportsPresencePenalty = known && protocol.SupportsPresencePenalty,
            SupportsFrequencyPenalty = known && protocol.SupportsFrequencyPenalty,
            MaxStopSequences = known ? protocol.MaxStopSequences : 0,
            Tools = protocol.Tools with { StrictInputSchema = known ? protocol.Tools.StrictInputSchema : LlmStrictInputSchemaSupport.Unknown }
        };
        LlmProviderCapabilities capabilities = LlmCapabilityOverrideLookup.Apply(defaults, connection, deployment, usageDeltasImplemented);
        if (!LlmCapabilityOverrideLookup.Overrides(connection, deployment, value => value.SupportsReasoningText))
            capabilities = capabilities with { SupportsReasoningText = known && profile.IsReasoning && protocol.SupportsReasoningText };
        if (capabilities.SupportsModelListing || capabilities.SupportsAccountUsage)
            throw new NotSupportedException("Azure does not implement deployment discovery or queried account usage.");
        return responses ? capabilities with
        {
            SupportsTopK = false,
            SupportsSeed = false,
            SupportsPresencePenalty = false,
            SupportsFrequencyPenalty = false,
            MaxStopSequences = 0
        } : capabilities;
    }

    internal static void ValidateSampling(LlmConnection connection, LlmChatRequest request) =>
        OpenAiModelProfiles.ValidateSampling(request with { ModelId = connection.Azure!.ModelProfileId ?? string.Empty });
}
