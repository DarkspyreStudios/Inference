using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Capabilities the shared Gemini protocol implements on every route.</summary>
internal static class GeminiCapabilities
{
    public static LlmProviderCapabilities Create(GeminiModelProfile profile, bool supportsTopK)
    {
        LlmRichContentSupported rich = profile.MultimodalFunctionResponses ? RichContentRoutes.GeminiFunctionResponse
            : RichContentRoutes.TextOnly with { ErrorFlag = LlmRichContentCarriage.Native };
        return new()
        {
            SupportsStreaming = true,
            SupportsReasoningText = true,
            SupportsStreamingUsage = true,
            SupportsUsageDeltas = true,
            SupportsModelListing = true,
            PromptCache = PromptCaching.Automatic,
            OutputLimitEnforcement = LlmOutputLimitEnforcement.ProviderEnforced,
            SupportsJsonSchema = true,
            SupportsTemperature = profile.AllowsSampling,
            SupportsTopP = profile.AllowsSampling,
            SupportsTopK = profile.AllowsSampling && supportsTopK,
            SupportsSeed = true,
            SupportsPresencePenalty = profile.AllowsSampling,
            SupportsFrequencyPenalty = profile.AllowsSampling,
            MaxStopSequences = 5,
            SupportsToolUse = true,
            Tools = new()
            {
                ChoiceModes = Enum.GetValues<LlmToolChoiceMode>().ToFrozenSet(),
                StrictInputSchema = LlmStrictInputSchemaSupport.Unsupported,
                SupportsParallelCalls = true,
                SupportsParallelCallControl = false,
                SupportsArgumentStreaming = false
            },
            ReasoningLevels = profile.Levels,
            SupportsPromptImages = true,
            SupportsPromptDocuments = true,
            RichContentSupported = rich,
            SupportsAgentSessions = true,
            SupportsAgentToolUse = true,
            SupportsAgentTurnInstructions = true,
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
