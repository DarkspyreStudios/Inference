using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>How a transport carries tool calls and tool results that are already in the history.</summary>
internal enum LlmToolHistory
{
    /// <summary>As the provider's own tool-call and tool-result items.</summary>
    Structured,

    /// <summary>As labelled text blocks in a flattened transcript; the provider runs no host tools.</summary>
    Text
}

/// <summary>Features a transport/model combination can accept without silent degradation.</summary>
public sealed record LlmProviderCapabilities
{
    /// <summary>Enforcement of MaxOutputTokens, independently of context output reserves.</summary>
    public LlmOutputLimitEnforcement OutputLimitEnforcement { get; init; }

    /// <summary>
    /// Supported means the adapter streams provider output as the model generates it. Unsupported makes it send the
    /// provider's non-streaming request and emit the complete response as events after it arrives. A transport whose
    /// provider has no non-streaming request rejects every request while this is Unsupported. Unknown permits an attempt.
    /// </summary>
    public InferenceSupport SupportsStreaming { get; init; }

    /// <summary>A streamed response reports usage by its completion.</summary>
    public InferenceSupport SupportsStreamingUsage { get; init; }

    /// <summary>A streamed response can emit cumulative usage observations before completion.</summary>
    public InferenceSupport SupportsUsageDeltas { get; init; }

    /// <summary>Public reasoning text can arrive separately from the answer. Unsupported suppresses display, not retained state.</summary>
    public InferenceSupport SupportsReasoningText { get; init; }

    public InferenceSupport SupportsModelListing { get; init; }

    public InferenceSupport SupportsAccountUsage { get; init; }

    public InferenceSupport SupportsJsonSchema { get; init; }

    public InferenceSupport SupportsJsonObject { get; init; }

    public LlmPromptCacheCapabilities PromptCache { get; init; } = new();

    /// <summary>The route implements explicit native cache selection. Overrides cannot enable it.</summary>
    public InferenceSupport SupportsExplicitCache { get; init; }

    /// <summary>The reasoning levels the model accepts. Empty when it accepts no reasoning control.</summary>
    public IReadOnlySet<LlmReasoningLevel> ReasoningLevels { get; init; } = FrozenSet<LlmReasoningLevel>.Empty;

    /// <summary>Support for reasoning above <see cref="LlmReasoningLevel.None"/>. An empty level set leaves support unknown.</summary>
    public InferenceSupport SupportsReasoning => ReasoningLevels.Count == 0 ? InferenceSupport.Unknown
        : ReasoningLevels.Any(static level => level > LlmReasoningLevel.None) ? InferenceSupport.Supported : InferenceSupport.Unsupported;

    public InferenceSupport SupportsTemperature { get; init; }

    public InferenceSupport SupportsTopP { get; init; }

    public InferenceSupport SupportsTopK { get; init; }

    public InferenceSupport SupportsSeed { get; init; }

    public InferenceSupport SupportsPresencePenalty { get; init; }

    public InferenceSupport SupportsFrequencyPenalty { get; init; }

    /// <summary>Zero disables string stops; int.MaxValue means no adapter-imposed limit.</summary>
    public int MaxStopSequences { get; init; }

    /// <summary>
    /// Supports host-executed tools: the provider can return tool calls and accept their results
    /// on a subsequent chat request. The chat caller executes tools; managed agent loops invoke handlers.
    /// </summary>
    public InferenceSupport SupportsToolUse { get; init; }

    /// <summary>The route implements shell definitions, calls and typed results.</summary>
    public InferenceSupport SupportsShellTools { get; init; }

    public LlmToolCapabilities Tools { get; init; } = new();

    public IReadOnlySet<LlmHostedToolKind> HostedTools { get; init => field = value.ToFrozenSet(); } = FrozenSet<LlmHostedToolKind>.Empty;

    /// <summary>How tool calls and results already in the history reach the provider.</summary>
    internal LlmToolHistory ToolHistory { get; init; }

    /// <summary>Supports the session-oriented <see cref="LlmAgentProviderService"/> contract.</summary>
    public InferenceSupport SupportsAgentSessions { get; init; }

    /// <summary>Agent sessions can invoke host-defined tools through an in-turn callback.</summary>
    public InferenceSupport SupportsAgentToolUse { get; init; }

    public InferenceSupport SupportsAgentTurnInstructions { get; init; }

    public LlmAgentCapabilities Agent { get; init; } = new();

    /// <summary>User prompts can include provider-neutral image attachments.</summary>
    public InferenceSupport SupportsPromptImages { get; init; }

    /// <summary>User prompts can include provider-neutral document attachments.</summary>
    public InferenceSupport SupportsPromptDocuments { get; init; }

    /// <summary>The selected route accepts provider-native file references. Overrides cannot enable an unimplemented route.</summary>
    public InferenceSupport SupportsFileReferences { get; init; }

    /// <summary>Native file resource operations, independently of generation attachment support.</summary>
    public LlmFileCapabilities Files { get; init; } = new();

    /// <summary>How the route carries each kind of tool-output content.</summary>
    internal LlmRichContentSupported RichContentSupported { get; init; } = new();

    public LlmProviderCapabilities Apply(LlmCapabilityOverrides? overrides)
    {
        if (MaxStopSequences < 0 || overrides?.MaxStopSequences < 0)
        { throw new ArgumentOutOfRangeException(nameof(overrides), "The stop-sequence limit cannot be negative."); }
        Agent.Validate();
        _ = Restrict(overrides?.SupportsAgentSessions, SupportsAgentSessions);
        _ = Restrict(overrides?.SupportsAgentToolUse, SupportsAgentToolUse);
        _ = Restrict(overrides?.SupportsAgentTurnInstructions, SupportsAgentTurnInstructions);
        if (((SupportsAgentSessions == InferenceSupport.Unsupported) && (SupportsAgentToolUse == InferenceSupport.Supported || SupportsAgentTurnInstructions == InferenceSupport.Supported
                || !Agent.IsNone))
            || (overrides?.SupportsAgentSessions is InferenceSupport.Unsupported
                && (overrides.SupportsAgentToolUse is InferenceSupport.Supported || overrides.SupportsAgentTurnInstructions is InferenceSupport.Supported)))
        {
            throw new ArgumentException("Agent operations require an enabled agent session lane.");
        }

        return overrides is null
        ? this
        : this with
        {
            SupportsStreaming = overrides.SupportsStreaming ?? SupportsStreaming,
            SupportsStreamingUsage = overrides.SupportsStreamingUsage ?? SupportsStreamingUsage,
            SupportsUsageDeltas = overrides.SupportsUsageDeltas ?? SupportsUsageDeltas,
            SupportsReasoningText = overrides.SupportsReasoningText ?? SupportsReasoningText,
            SupportsModelListing = overrides.SupportsModelListing ?? SupportsModelListing,
            SupportsAccountUsage = overrides.SupportsAccountUsage ?? SupportsAccountUsage,
            SupportsJsonSchema = overrides.SupportsJsonSchema ?? SupportsJsonSchema,
            SupportsJsonObject = overrides.SupportsJsonObject ?? SupportsJsonObject,
            ReasoningLevels = overrides.ReasoningLevels ?? ReasoningLevels,
            SupportsTemperature = overrides.SupportsTemperature ?? SupportsTemperature,
            SupportsTopP = overrides.SupportsTopP ?? SupportsTopP,
            SupportsTopK = overrides.SupportsTopK ?? SupportsTopK,
            SupportsSeed = overrides.SupportsSeed ?? SupportsSeed,
            SupportsPresencePenalty = overrides.SupportsPresencePenalty ?? SupportsPresencePenalty,
            SupportsFrequencyPenalty = overrides.SupportsFrequencyPenalty ?? SupportsFrequencyPenalty,
            MaxStopSequences = overrides.MaxStopSequences ?? MaxStopSequences,
            SupportsToolUse = overrides.SupportsToolUse ?? SupportsToolUse,
            Tools = overrides.SupportsToolUse is InferenceSupport.Unsupported ? new() : Tools,
            SupportsAgentSessions = Restrict(overrides.SupportsAgentSessions, SupportsAgentSessions),
            SupportsAgentToolUse = Intersect(Restrict(overrides.SupportsAgentSessions, SupportsAgentSessions),
                Restrict(overrides.SupportsAgentToolUse, SupportsAgentToolUse)),
            SupportsAgentTurnInstructions = Intersect(Restrict(overrides.SupportsAgentSessions, SupportsAgentSessions),
                Restrict(overrides.SupportsAgentTurnInstructions, SupportsAgentTurnInstructions)),
            Agent = Restrict(overrides.SupportsAgentSessions, SupportsAgentSessions) != InferenceSupport.Unsupported
                ? Agent with
                {
                    RichContentSupported = Agent.RichContentSupported.Restrict(
                        overrides.SupportsToolResultImages, overrides.SupportsToolResultDocuments)
                }
                : new(),
            SupportsPromptImages = overrides.SupportsPromptImages ?? SupportsPromptImages,
            SupportsPromptDocuments = overrides.SupportsPromptDocuments ?? SupportsPromptDocuments,
            RichContentSupported = RichContentSupported.Restrict(
                overrides.SupportsToolResultImages, overrides.SupportsToolResultDocuments)
        };
    }

    internal static InferenceSupport Intersect(InferenceSupport first, InferenceSupport second) =>
        first == InferenceSupport.Unsupported || second == InferenceSupport.Unsupported ? InferenceSupport.Unsupported
        : first == InferenceSupport.Unknown || second == InferenceSupport.Unknown ? InferenceSupport.Unknown
        : InferenceSupport.Supported;

    private static InferenceSupport Restrict(InferenceSupport? requested, InferenceSupport implemented)
    {
        if (requested is InferenceSupport.Supported && implemented == InferenceSupport.Unsupported)
        {
            throw new ArgumentException("Capability overrides cannot enable unimplemented agent operations.");
        }

        return requested ?? implemented;
    }
}

/// <summary>Connection-specific corrections for an otherwise OpenAI-compatible endpoint.</summary>
public sealed record LlmCapabilityOverrides
{
    /// <summary>False selects the provider's non-streaming request.</summary>
    public InferenceSupport? SupportsStreaming { get; init; }

    public InferenceSupport? SupportsStreamingUsage { get; init; }

    /// <summary>Enables continuous usage on a compatible endpoint, or disables an adapter's running usage events.</summary>
    public InferenceSupport? SupportsUsageDeltas { get; init; }

    /// <summary>False suppresses public reasoning events and summary requests without dropping continuation state.</summary>
    public InferenceSupport? SupportsReasoningText { get; init; }

    public InferenceSupport? SupportsModelListing { get; init; }

    public InferenceSupport? SupportsAccountUsage { get; init; }

    public InferenceSupport? SupportsJsonSchema { get; init; }

    public InferenceSupport? SupportsJsonObject { get; init; }

    /// <summary>The reasoning levels the endpoint accepts, replacing the transport's own set.</summary>
    public IReadOnlySet<LlmReasoningLevel>? ReasoningLevels { get; init; }

    public InferenceSupport? SupportsTemperature { get; init; }

    public InferenceSupport? SupportsTopP { get; init; }

    public InferenceSupport? SupportsTopK { get; init; }

    public InferenceSupport? SupportsSeed { get; init; }

    public InferenceSupport? SupportsPresencePenalty { get; init; }

    public InferenceSupport? SupportsFrequencyPenalty { get; init; }

    public int? MaxStopSequences { get; init; }

    public InferenceSupport? SupportsToolUse { get; init; }

    public InferenceSupport? SupportsAgentSessions { get; init; }

    public InferenceSupport? SupportsAgentToolUse { get; init; }

    public InferenceSupport? SupportsAgentTurnInstructions { get; init; }

    public InferenceSupport? SupportsPromptImages { get; init; }

    public InferenceSupport? SupportsPromptDocuments { get; init; }

    /// <summary>False stops sending tool-result images; an override cannot enable them.</summary>
    public InferenceSupport? SupportsToolResultImages { get; init; }

    /// <summary>False stops sending tool-result documents; an override cannot enable them.</summary>
    public InferenceSupport? SupportsToolResultDocuments { get; init; }
}
