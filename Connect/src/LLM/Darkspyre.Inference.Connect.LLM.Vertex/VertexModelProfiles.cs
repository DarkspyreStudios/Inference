using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;
using Darkspyre.Inference.Connect.LLM.Providers;

namespace Darkspyre.Inference.Connect.LLM.Vertex;

internal sealed record VertexModelProfile(string Id, long? ContextTokens, int? OutputTokens, LlmProviderCapabilities Capabilities)
{
    public bool IsClaude => Id.StartsWith("anthropic/", StringComparison.Ordinal);
    public IReadOnlySet<LlmReasoningLevel> ReasoningLevels => Capabilities.ReasoningLevels;
}

internal static class VertexModelProfiles
{
    public static VertexModelProfile Get(string modelId)
    {
        var (publisher, model) = VertexEndpoints.ParseModel(modelId);
        if (publisher == "google" && GeminiModels.IsTextChat(model))
        {
            var (context, output) = GeminiModels.VertexLimits(model);
            return new(modelId, context, output,
                GeminiCapabilities.Create(GeminiModelProfiles.Get(model, catalogThinking: null), supportsTopK: true));
        }
        if (publisher != "anthropic" || !model.StartsWith("claude-", StringComparison.Ordinal))
            throw new NotSupportedException("Vertex serves google/gemini-* and anthropic/claude-* text models.");

        AnthropicModelProfile profile = AnthropicModelProfiles.Resolve(model.Split('@')[0], null);
        LlmRichContentSupported rich = RichContentRoutes.Anthropic with { MaxBinaryBytes = 5 * 1024 * 1024 };
        var capabilities = new LlmProviderCapabilities
        {
            SupportsStreaming = InferenceSupport.Supported,
            SupportsReasoningText = InferenceSupport.Supported,
            SupportsStreamingUsage = InferenceSupport.Supported,
            SupportsUsageDeltas = InferenceSupport.Supported,
            PromptCache = PromptCaching.Claude,
            SupportsModelListing = InferenceSupport.Supported,
            OutputLimitEnforcement = LlmOutputLimitEnforcement.ProviderEnforced,
            SupportsJsonSchema = profile.SupportsStructuredOutputs ?? InferenceSupport.Unknown,
            SupportsTemperature = (profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsTopP = (profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsTopK = (profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            MaxStopSequences = int.MaxValue,
            SupportsToolUse = InferenceSupport.Supported,
            Tools = new()
            {
                ChoiceModes = Enum.GetValues<LlmToolChoiceMode>()
                    .Where(mode => profile.AllowsForcedToolChoice || mode is not (LlmToolChoiceMode.Required or LlmToolChoiceMode.Specific)).ToFrozenSet(),
                StrictInputSchema = profile.SupportsStructuredOutputs switch
                {
                    InferenceSupport.Supported => LlmStrictInputSchemaSupport.Supported,
                    InferenceSupport.Unsupported => LlmStrictInputSchemaSupport.Unsupported,
                    _ => LlmStrictInputSchemaSupport.Unknown
                },
                SupportsParallelCalls = InferenceSupport.Supported,
                SupportsParallelCallControl = InferenceSupport.Supported,
                SupportsArgumentStreaming = InferenceSupport.Supported
            },
            ReasoningLevels = profile.ReasoningLevels,
            SupportsPromptImages = InferenceSupport.Supported,
            SupportsPromptDocuments = InferenceSupport.Supported,
            RichContentSupported = rich,
            SupportsAgentSessions = InferenceSupport.Supported,
            SupportsAgentToolUse = InferenceSupport.Supported,
            SupportsAgentTurnInstructions = InferenceSupport.Supported,
            Agent = ManagedAgentPolicy.Capabilities with { RichContentSupported = rich }
        };
        return new(modelId, profile.MaxInputTokens,
            profile.MaxOutputTokens is > 0 and <= int.MaxValue ? (int)profile.MaxOutputTokens.Value : null, capabilities);
    }
}
