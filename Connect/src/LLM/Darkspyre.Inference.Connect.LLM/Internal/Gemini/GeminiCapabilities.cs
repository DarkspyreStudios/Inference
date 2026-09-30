using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Capabilities the shared Gemini protocol implements on every route.</summary>
internal static class GeminiCapabilities
{
    public static LlmProviderCapabilities Create(IReadOnlySet<LlmReasoningLevel> reasoningLevels)
    {
        LlmRichContentSupported rich = RichContentRoutes.TextOnly with { ErrorFlag = LlmRichContentCarriage.Native };
        return new()
        {
            SupportsStreaming = true,
            SupportsStreamingUsage = true,
            SupportsUsageDeltas = true,
            SupportsModelListing = true,
            OutputLimitEnforcement = LlmOutputLimitEnforcement.ProviderEnforced,
            SupportsJsonSchema = true,
            SupportsTemperature = true,
            SupportsToolUse = true,
            Tools = new()
            {
                ChoiceModes = Enum.GetValues<LlmToolChoiceMode>().ToFrozenSet(),
                StrictInputSchema = LlmStrictInputSchemaSupport.Unsupported,
                SupportsParallelCalls = true,
                SupportsParallelCallControl = false,
                SupportsArgumentStreaming = false
            },
            ReasoningLevels = reasoningLevels,
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
            : mime is "application/pdf" or "text/plain";
        int max = mime == "application/pdf" ? 50 * 1024 * 1024 : 7 * 1024 * 1024;
        return supported && attachment.Data.Length <= max;
    }
}
