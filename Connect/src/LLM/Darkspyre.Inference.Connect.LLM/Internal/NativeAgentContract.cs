namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Current native-runtime declarations and fail-fast managed-only option checks.</summary>
internal static class NativeAgentContract
{
    public static LlmAgentCapabilities Claude { get; } = new()
    {
        ContextOwnership = LlmAgentContextOwnership.Provider,
        SupportsNativeResume = true,
        NativeFork = LlmAgentNativeFork.CurrentPosition,
        CompactionOwnership = LlmAgentCompactionOwnership.Provider,
        SupportsInterrupt = true,
        ToolExecutionOwnership = LlmAgentToolExecutionOwnership.Mixed
    };

    public static LlmAgentCapabilities AgentCore { get; } = new()
    {
        ContextOwnership = LlmAgentContextOwnership.Provider,
        SupportsNativeResume = true,
        CompactionOwnership = LlmAgentCompactionOwnership.Provider,
        ToolExecutionOwnership = LlmAgentToolExecutionOwnership.Mixed
    };

    public static void ValidateStart(LlmAgentSessionOptions options)
    {
        if (options.Context is not null || options.ThreadId is not null || options.SessionId is not null
            || options.ExecutionOptions is not null)
        {
            throw new NotSupportedException(
                "This native runtime does not accept host context, host identities, or managed execution options. "
                + "Use native resume/fork for provider-owned conversations.");
        }

        ValidateTools(options.Tools);
    }

    public static void ValidateConnection(
        LlmConnection connection,
        LlmAgentCapabilities agent,
        string? modelId,
        IReadOnlyList<LlmAgentToolDefinition> tools,
        LlmAgentSessionHandlers handlers)
    {
        var implemented = new LlmProviderCapabilities
        {
            SupportsAgentSessions = true,
            SupportsAgentToolUse = true,
            Agent = agent
        };
        var effective = implemented.Apply(connection.CapabilityOverrides);
        if (modelId is not null && connection.ModelCapabilityOverrides.TryGetValue(modelId, out var modelOverrides))
        {
            effective = effective.Apply(modelOverrides);
        }
        if (!effective.SupportsAgentSessions || (!effective.SupportsAgentToolUse
            && (tools.Count > 0 || handlers.ToolHandler is not null)))
        {
            throw new NotSupportedException("The connection/model configuration disables the requested agent operation.");
        }
    }

    public static void ValidateTools(IReadOnlyList<LlmAgentToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Any(tool => tool.RequiresSequentialExecution))
        {
            throw new NotSupportedException("This native runtime does not implement managed tool scheduling.");
        }
    }

    public static void ValidateTurn(LlmAgentTurnRequest request)
    {
        if (request.Instructions is not null)
        {
            throw new NotSupportedException(
                "This native runtime does not support per-turn instruction overrides. "
                + "Check SupportsAgentTurnInstructions before supplying Instructions.");
        }
    }

    public static NotSupportedException UnsupportedContext(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new NotSupportedException(
            "This runtime owns its conversation. Host context capture, status, replacement, and "
            + "compaction continuation are not supported; check GetCapabilitiesAsync.");
    }
}
