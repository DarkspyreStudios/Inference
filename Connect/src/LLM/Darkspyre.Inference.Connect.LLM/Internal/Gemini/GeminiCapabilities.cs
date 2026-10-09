using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Capabilities the shared Gemini protocol implements on every route.</summary>
internal static class GeminiCapabilities
{
    public static LlmProviderCapabilities Create(GeminiModelProfile profile, InferenceSupport supportsTopK)
    {
        LlmRichContentSupported rich = profile.MultimodalFunctionResponses != InferenceSupport.Unsupported ? RichContentRoutes.GeminiFunctionResponse
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
            SupportsJsonSchema = profile.Control == GeminiThinkingControl.Unknown ? InferenceSupport.Unknown : InferenceSupport.Supported,
            SupportsJsonObject = InferenceSupport.Unsupported,
            SupportsAccountUsage = InferenceSupport.Unsupported,
            SupportsTemperature = (profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsTopP = (profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            SupportsTopK = profile.AllowsSampling ? supportsTopK : InferenceSupport.Unsupported,
            SupportsSeed = InferenceSupport.Supported,
            SupportsPresencePenalty = profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unknown,
            SupportsFrequencyPenalty = profile.AllowsSampling ? InferenceSupport.Supported : InferenceSupport.Unknown,
            MaxStopSequences = 5,
            SupportsToolUse = InferenceSupport.Supported,
            Tools = new()
            {
                ChoiceModes = Enum.GetValues<LlmToolChoiceMode>().ToFrozenSet(),
                // Enforcement depends on the model, schema and request-wide mode, not a per-tool strict field.
                StrictInputSchema = LlmStrictInputSchemaSupport.Unknown,
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

    /// <summary>Route-specific inline media. The Gemini API's total request limit is not a per-image limit.</summary>
    public static bool AcceptsAttachment(LlmAttachment attachment, bool vertex)
    {
        string mime = LlmAttachments.ResolveMediaType(attachment);
        if (attachment.Kind == LlmBinaryKind.Image)
            return AcceptsImageMediaType(mime, vertex)
                && (!vertex || attachment.Data.Length <= 7 * 1024 * 1024);
        return mime == "application/pdf" ? attachment.Data.Length <= 50 * 1024 * 1024 : LlmAttachments.IsTextMediaType(mime);
    }

    /// <summary>Prompt image MIME admission, independently of tool-result formats and unverified Vertex acceptance.</summary>
    public static bool AcceptsImageMediaType(string mime, bool vertex) =>
        vertex ? mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            : mime is "image/png" or "image/jpeg" or "image/webp" or "image/heic" or "image/heif";
}
