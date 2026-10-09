using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Capabilities the shared Gemini protocol implements on every route.</summary>
internal static class GeminiCapabilities
{
    public static LlmProviderCapabilities Create(GeminiModelProfile profile, InferenceSupport supportsTopK)
    {
        LlmRichContentSupported rich = profile.MultimodalFunctionResponses ? RichContentRoutes.GeminiFunctionResponse
            : RichContentRoutes.TextOnly with { ErrorFlag = LlmRichContentCarriage.Native };
        return new()
        {
            SupportsStreaming = InferenceSupport.Supported,
            SupportsReasoningText = InferenceSupport.Supported,
            SupportsStreamingUsage = InferenceSupport.Supported,
            SupportsUsageDeltas = InferenceSupport.Supported,
            SupportsModelListing = InferenceSupport.Supported,
            PromptCache = PromptCaching.Automatic,
            OutputLimitEnforcement = LlmOutputLimitEnforcement.ProviderEnforced,
            SupportsJsonSchema = InferenceSupport.Supported,
            SupportsJsonObject = InferenceSupport.Unsupported,
            SupportsAccountUsage = InferenceSupport.Unsupported,
            SupportsTemperature = (profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsTopP = (profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsTopK = profile.AllowsSampling ? supportsTopK : InferenceSupport.Unsupported,
            SupportsSeed = InferenceSupport.Supported,
            SupportsPresencePenalty = (profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsFrequencyPenalty = (profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            MaxStopSequences = 5,
            SupportsToolUse = InferenceSupport.Supported,
            Tools = new()
            {
                ChoiceModes = Enum.GetValues<LlmToolChoiceMode>().ToFrozenSet(),
                StrictInputSchema = LlmStrictInputSchemaSupport.Unsupported,
                SupportsParallelCalls = InferenceSupport.Supported,
                SupportsParallelCallControl = InferenceSupport.Unsupported,
                SupportsArgumentStreaming = InferenceSupport.Unsupported
            },
            ReasoningLevels = profile.Levels,
            SupportsPromptImages = InferenceSupport.Supported,
            SupportsPromptDocuments = InferenceSupport.Supported,
            RichContentSupported = rich,
            SupportsAgentSessions = InferenceSupport.Supported,
            SupportsAgentToolUse = InferenceSupport.Supported,
            SupportsAgentTurnInstructions = InferenceSupport.Supported,
            Agent = ManagedAgentPolicy.Capabilities with { RichContentSupported = rich }
        };
    }

    /// <summary>Inline media the Gemini routes accept: PNG, JPEG and WebP images up to 7 MiB, PDFs up to 50 MiB, text up to 7 MiB.</summary>
    public static bool AcceptsAttachment(LlmAttachment attachment)
    {
        string mime = LlmAttachments.ResolveMediaType(attachment);
        bool supported = attachment.Kind == LlmBinaryKind.Image
            ? mime is "image/png" or "image/jpeg" or "image/webp"
            : mime == "application/pdf" || LlmAttachments.IsTextMediaType(mime);
        int max = mime == "application/pdf" ? 50 * 1024 * 1024 : 7 * 1024 * 1024;
        return supported && attachment.Data.Length <= max;
    }
}
