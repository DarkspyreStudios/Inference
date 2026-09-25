namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Current native-runtime declarations and fail-fast managed-only option checks.</summary>
internal static class NativeAgentContract
{
    public static LlmAgentCapabilities Claude { get; } = new()
    {
        ContextOwnership = LlmAgentOwnership.Provider,
        ContextImport = LlmAgentContextImport.OpeningContext,
        SupportsNativeResume = true,
        NativeFork = LlmAgentNativeFork.CurrentPosition,
        CompactionOwnership = LlmAgentOwnership.Provider,
        SupportsInterrupt = true,
        ToolExecutionOwnership = LlmAgentToolExecutionOwnership.Mixed,
        RichContentSupported = RichContentRoutes.ClaudeMcp,
        ResourceObservation = LlmOperationSupport.Supported
    };

    public static LlmAgentCapabilities AgentCore { get; } = new()
    {
        ContextOwnership = LlmAgentOwnership.Provider,
        ContextImport = LlmAgentContextImport.OpeningContext,
        SupportsNativeResume = true,
        CompactionOwnership = LlmAgentOwnership.Provider,
        ToolExecutionOwnership = LlmAgentToolExecutionOwnership.Mixed,
        RichContentSupported = RichContentRoutes.AgentCore,
        // Not measured: no AgentCore credentials were available.
        ResourceObservation = LlmOperationSupport.Unknown
    };

    public static LlmAgentNativeRuntimeOptions ValidateStart(LlmAgentSessionOptions options)
    {
        // A supplied context becomes opening context; see AgentOpeningContext.
        if (options.ThreadId is not null || options.SessionId is not null || options.ExecutionOptions is not null)
        {
            throw new NotSupportedException(
                "This native runtime does not accept host identities or managed execution options. "
                + "Use native resume/fork for provider-owned conversations.");
        }

        ValidateTools(options.Tools);
        return options.Runtime ?? throw new ArgumentException(
            "This native runtime requires Runtime options with a working directory.", nameof(options));
    }

    /// <summary>The resume settings: the supplied runtime whole, or the documented resume defaults.</summary>
    public static LlmAgentNativeRuntimeOptions ResumeRuntime(LlmAgentResumeOptions options) =>
        options.Runtime ?? new()
        {
            WorkingDirectory = Environment.CurrentDirectory,
            WritableRoots = [Environment.CurrentDirectory]
        };

    public static void ValidateConnection(
        LlmConnection connection,
        LlmAgentCapabilities agent,
        string? modelId,
        IReadOnlyList<LlmAgentToolDefinition> tools,
        LlmAgentToolHandler? toolHandler)
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
            && (tools.Count > 0 || toolHandler is not null)))
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
