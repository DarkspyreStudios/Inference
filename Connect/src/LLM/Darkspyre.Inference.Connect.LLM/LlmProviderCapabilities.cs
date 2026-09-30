using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>How a transport carries tool calls and tool results that are already in the history.</summary>
public enum LlmToolHistory
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
    /// True when the adapter streams provider output as the model generates it. False makes the adapter send the
    /// provider's non-streaming request and emit the complete response as events after it arrives. A transport whose
    /// provider has no non-streaming request rejects every request while this is false.
    /// </summary>
    public bool SupportsStreaming { get; init; }

    /// <summary>A streamed response reports usage by its completion.</summary>
    public bool SupportsStreamingUsage { get; init; }

    /// <summary>A streamed response can emit cumulative usage observations before completion.</summary>
    public bool SupportsUsageDeltas { get; init; }

    /// <summary>Public reasoning text can arrive separately from the answer. False suppresses display, not retained state.</summary>
    public bool SupportsReasoningText { get; init; }

    public bool SupportsModelListing { get; init; }

    public bool SupportsAccountUsage { get; init; }

    public bool SupportsJsonSchema { get; init; }

    public bool SupportsJsonObject { get; init; }

    /// <summary>The reasoning levels the model accepts. Empty when it accepts no reasoning control.</summary>
    public IReadOnlySet<LlmReasoningLevel> ReasoningLevels { get; init; } = FrozenSet<LlmReasoningLevel>.Empty;

    /// <summary>True when the model accepts a reasoning level above <see cref="LlmReasoningLevel.None"/>.</summary>
    public bool SupportsReasoning => ReasoningLevels.Any(static level => level > LlmReasoningLevel.None);

    public bool SupportsTemperature { get; init; }

    /// <summary>
    /// Supports host-executed tools: the provider can return tool calls and accept their results
    /// on a subsequent chat request. The chat caller executes tools; managed agent loops invoke handlers.
    /// </summary>
    public bool SupportsToolUse { get; init; }

    public LlmToolCapabilities Tools { get; init; } = new();

    /// <summary>How tool calls and results already in the history reach the provider.</summary>
    public LlmToolHistory ToolHistory { get; init; }

    /// <summary>Supports the session-oriented <see cref="ILlmAgentClient"/> contract.</summary>
    public bool SupportsAgentSessions { get; init; }

    /// <summary>Agent sessions can invoke host-defined tools through an in-turn callback.</summary>
    public bool SupportsAgentToolUse { get; init; }

    public bool SupportsAgentTurnInstructions { get; init; }

    public LlmAgentCapabilities Agent { get; init; } = new();

    /// <summary>User prompts can include provider-neutral image attachments.</summary>
    public bool SupportsPromptImages { get; init; }

    /// <summary>User prompts can include provider-neutral document attachments.</summary>
    public bool SupportsPromptDocuments { get; init; }

    /// <summary>How the route carries each kind of tool-output content.</summary>
    public LlmRichContentSupported RichContentSupported { get; init; } = new();

    public LlmProviderCapabilities Apply(LlmCapabilityOverrides? overrides)
    {
        Agent.Validate();
        _ = Restrict(overrides?.SupportsAgentSessions, SupportsAgentSessions);
        _ = Restrict(overrides?.SupportsAgentToolUse, SupportsAgentToolUse);
        _ = Restrict(overrides?.SupportsAgentTurnInstructions, SupportsAgentTurnInstructions);
        if ((!SupportsAgentSessions && (SupportsAgentToolUse || SupportsAgentTurnInstructions
                || !Agent.IsNone))
            || (overrides?.SupportsAgentSessions is false
                && (overrides.SupportsAgentToolUse is true || overrides.SupportsAgentTurnInstructions is true)))
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
            SupportsToolUse = overrides.SupportsToolUse ?? SupportsToolUse,
            Tools = overrides.SupportsToolUse is false ? new() : Tools,
            SupportsAgentSessions = Restrict(overrides.SupportsAgentSessions, SupportsAgentSessions),
            SupportsAgentToolUse = Restrict(overrides.SupportsAgentSessions, SupportsAgentSessions)
                && Restrict(overrides.SupportsAgentToolUse, SupportsAgentToolUse),
            SupportsAgentTurnInstructions = Restrict(overrides.SupportsAgentSessions, SupportsAgentSessions)
                && Restrict(overrides.SupportsAgentTurnInstructions, SupportsAgentTurnInstructions),
            Agent = Restrict(overrides.SupportsAgentSessions, SupportsAgentSessions)
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

    private static bool Restrict(bool? requested, bool implemented)
    {
        if (requested is true && !implemented)
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
    public bool? SupportsStreaming { get; init; }

    public bool? SupportsStreamingUsage { get; init; }

    /// <summary>Enables continuous usage on a compatible endpoint, or disables an adapter's running usage events.</summary>
    public bool? SupportsUsageDeltas { get; init; }

    /// <summary>False suppresses public reasoning events and summary requests without dropping continuation state.</summary>
    public bool? SupportsReasoningText { get; init; }

    public bool? SupportsModelListing { get; init; }

    public bool? SupportsAccountUsage { get; init; }

    public bool? SupportsJsonSchema { get; init; }

    public bool? SupportsJsonObject { get; init; }

    /// <summary>The reasoning levels the endpoint accepts, replacing the transport's own set.</summary>
    public IReadOnlySet<LlmReasoningLevel>? ReasoningLevels { get; init; }

    public bool? SupportsTemperature { get; init; }

    public bool? SupportsToolUse { get; init; }

    public bool? SupportsAgentSessions { get; init; }

    public bool? SupportsAgentToolUse { get; init; }

    public bool? SupportsAgentTurnInstructions { get; init; }

    public bool? SupportsPromptImages { get; init; }

    public bool? SupportsPromptDocuments { get; init; }

    /// <summary>False stops sending tool-result images; an override cannot enable them.</summary>
    public bool? SupportsToolResultImages { get; init; }

    /// <summary>False stops sending tool-result documents; an override cannot enable them.</summary>
    public bool? SupportsToolResultDocuments { get; init; }
}
