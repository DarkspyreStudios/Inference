using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Features a transport/model combination can accept without silent degradation.</summary>
public sealed record LlmProviderCapabilities
{
    public bool SupportsStreaming { get; init; }

    public bool SupportsStreamingUsage { get; init; }

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

    /// <summary>Host tool results can include provider-neutral image attachments.</summary>
    public bool SupportsToolResultImages { get; init; }

    /// <summary>Host tool results can include provider-neutral document attachments.</summary>
    public bool SupportsToolResultDocuments { get; init; }

    public LlmProviderCapabilities Apply(LlmCapabilityOverrides? overrides)
    {
        Agent.Validate();
        _ = Restrict(overrides?.SupportsAgentSessions, SupportsAgentSessions);
        _ = Restrict(overrides?.SupportsAgentToolUse, SupportsAgentToolUse);
        _ = Restrict(overrides?.SupportsAgentTurnInstructions, SupportsAgentTurnInstructions);
        if ((!SupportsAgentSessions && (SupportsAgentToolUse || SupportsAgentTurnInstructions
                || Agent != new LlmAgentCapabilities()))
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
            SupportsModelListing = overrides.SupportsModelListing ?? SupportsModelListing,
            SupportsAccountUsage = overrides.SupportsAccountUsage ?? SupportsAccountUsage,
            SupportsJsonSchema = overrides.SupportsJsonSchema ?? SupportsJsonSchema,
            SupportsJsonObject = overrides.SupportsJsonObject ?? SupportsJsonObject,
            ReasoningLevels = overrides.ReasoningLevels ?? ReasoningLevels,
            SupportsTemperature = overrides.SupportsTemperature ?? SupportsTemperature,
            SupportsToolUse = overrides.SupportsToolUse ?? SupportsToolUse,
            SupportsAgentSessions = Restrict(overrides.SupportsAgentSessions, SupportsAgentSessions),
            SupportsAgentToolUse = Restrict(overrides.SupportsAgentSessions, SupportsAgentSessions)
                && Restrict(overrides.SupportsAgentToolUse, SupportsAgentToolUse),
            SupportsAgentTurnInstructions = Restrict(overrides.SupportsAgentSessions, SupportsAgentSessions)
                && Restrict(overrides.SupportsAgentTurnInstructions, SupportsAgentTurnInstructions),
            Agent = Restrict(overrides.SupportsAgentSessions, SupportsAgentSessions) ? Agent : new(),
            SupportsPromptImages = overrides.SupportsPromptImages ?? SupportsPromptImages,
            SupportsPromptDocuments = overrides.SupportsPromptDocuments ?? SupportsPromptDocuments,
            SupportsToolResultImages = overrides.SupportsToolResultImages ?? SupportsToolResultImages,
            SupportsToolResultDocuments = overrides.SupportsToolResultDocuments
                ?? SupportsToolResultDocuments
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
    public bool? SupportsStreaming { get; init; }

    public bool? SupportsStreamingUsage { get; init; }

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

    public bool? SupportsToolResultImages { get; init; }

    public bool? SupportsToolResultDocuments { get; init; }
}
