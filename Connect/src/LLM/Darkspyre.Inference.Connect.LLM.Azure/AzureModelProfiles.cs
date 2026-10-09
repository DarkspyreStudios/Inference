using Darkspyre.Inference.Common;
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
        InferenceSupport ProfileSupport(InferenceSupport support) => known ? support
            : LlmProviderCapabilities.Intersect(support, InferenceSupport.Unknown);
        LlmProviderCapabilities defaults = protocol with
        {
            SupportsModelListing = InferenceSupport.Unsupported,
            SupportsAccountUsage = InferenceSupport.Unsupported,
            SupportsJsonSchema = ProfileSupport(protocol.SupportsJsonSchema),
            SupportsJsonObject = ProfileSupport(protocol.SupportsJsonObject),
            SupportsPromptImages = ProfileSupport(protocol.SupportsPromptImages),
            SupportsPromptDocuments = ProfileSupport(protocol.SupportsPromptDocuments),
            ReasoningLevels = known ? profile.ReasoningLevels : LlmReasoningEffort.Levels(),
            SupportsTemperature = known ? profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported : InferenceSupport.Unknown,
            SupportsTopP = known ? profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported : InferenceSupport.Unknown,
            SupportsTopK = ProfileSupport(protocol.SupportsTopK),
            SupportsSeed = ProfileSupport(protocol.SupportsSeed),
            SupportsPresencePenalty = ProfileSupport(protocol.SupportsPresencePenalty),
            SupportsFrequencyPenalty = ProfileSupport(protocol.SupportsFrequencyPenalty),
            MaxStopSequences = known ? protocol.MaxStopSequences : 0,
            Tools = protocol.Tools with { StrictInputSchema = known ? protocol.Tools.StrictInputSchema : LlmStrictInputSchemaSupport.Unknown }
        };
        LlmProviderCapabilities capabilities = LlmCapabilityOverrideLookup.Apply(defaults, connection, deployment, usageDeltasImplemented);
        if (!LlmCapabilityOverrideLookup.Overrides(connection, deployment, value => value.SupportsReasoningText))
            capabilities = capabilities with { SupportsReasoningText = known && !profile.IsReasoning ? InferenceSupport.Unsupported : ProfileSupport(protocol.SupportsReasoningText) };
        if ((capabilities.SupportsModelListing != InferenceSupport.Unsupported) || (capabilities.SupportsAccountUsage != InferenceSupport.Unsupported))
            throw new NotSupportedException("Azure does not implement deployment discovery or queried account usage.");
        return responses ? capabilities with
        {
            SupportsTopK = InferenceSupport.Unsupported,
            SupportsSeed = InferenceSupport.Unsupported,
            SupportsPresencePenalty = InferenceSupport.Unsupported,
            SupportsFrequencyPenalty = InferenceSupport.Unsupported,
            MaxStopSequences = 0
        } : capabilities;
    }

    internal static void ValidateSampling(LlmConnection connection, LlmChatRequest request) =>
        OpenAiModelProfiles.ValidateSampling(request with { ModelId = connection.Azure!.ModelProfileId ?? string.Empty });
}
