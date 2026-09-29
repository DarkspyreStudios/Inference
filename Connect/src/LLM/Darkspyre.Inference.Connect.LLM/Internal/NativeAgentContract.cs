using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Current native-runtime declarations and fail-fast managed-only option checks.</summary>
internal static class NativeAgentContract
{
    // Declared before the capability records that read it: static initializers run in textual order.
    private static IReadOnlySet<LlmAgentRuntimePolicyKind> HostControlled { get; } =
        FrozenSet.Create(LlmAgentRuntimePolicyKind.HostControlled);

    private static IReadOnlySet<LlmAgentRuntimePolicyKind> ProviderConfigured { get; } =
        FrozenSet.Create(LlmAgentRuntimePolicyKind.ProviderConfigured);

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
        SupportedRuntimePolicies = HostControlled,
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
        SupportedRuntimePolicies = HostControlled,
        // Not measured: no AgentCore credentials were available.
        ResourceObservation = LlmOperationSupport.Unknown
    };

    public static LlmAgentCapabilities Antigravity { get; } = new()
    {
        ContextOwnership = LlmAgentOwnership.Provider,
        SupportsNativeResume = true,
        CompactionOwnership = LlmAgentOwnership.Provider,
        ToolExecutionOwnership = LlmAgentToolExecutionOwnership.Provider,
        RichContentSupported = RichContentRoutes.TextOnly,
        SupportedRuntimePolicies = ProviderConfigured
    };

    /// <summary>The validated start settings of a runtime that declares <paramref name="agent"/>.</summary>
    public static LlmAgentNativeRuntimeOptions ValidateStart(LlmAgentSessionOptions options, LlmAgentCapabilities agent)
    {
        // A supplied context becomes opening context; see AgentOpeningContext.
        if (options.ThreadId is not null || options.SessionId is not null || options.ExecutionOptions is not null)
        {
            throw new NotSupportedException(
                "This native runtime does not accept host identities or managed execution options. "
                + "Use native resume/fork for provider-owned conversations.");
        }

        ValidateTools(options.Tools);
        LlmAgentNativeRuntimeOptions runtime = options.Runtime ?? throw new ArgumentException(
            "This native runtime requires Runtime options with a working directory.", nameof(options));
        ValidateRuntime(agent, runtime, options.ResourceOutputRetentionBytes);
        return runtime;
    }

    /// <summary>
    /// The validated resume settings: the supplied runtime whole, or the documented host-controlled resume
    /// defaults. A runtime that does not accept host-controlled policy therefore rejects a null runtime.
    /// </summary>
    public static LlmAgentNativeRuntimeOptions ResumeRuntime(LlmAgentResumeOptions options, LlmAgentCapabilities agent)
    {
        LlmAgentNativeRuntimeOptions runtime = options.Runtime ?? new()
        {
            WorkingDirectory = Environment.CurrentDirectory,
            Policy = new LlmAgentHostRuntimePolicy { WritableRoots = [Environment.CurrentDirectory] }
        };
        ValidateRuntime(agent, runtime, options.ResourceOutputRetentionBytes);
        return runtime;
    }

    /// <summary>The host-controlled policy of settings that <see cref="ValidateRuntime"/> accepted for a host-only runtime.</summary>
    public static LlmAgentHostRuntimePolicy HostPolicy(LlmAgentNativeRuntimeOptions runtime) =>
        runtime.Policy as LlmAgentHostRuntimePolicy
        ?? throw new NotSupportedException("This native runtime accepts only a host-controlled runtime policy.");

    private static void ValidateRuntime(
        LlmAgentCapabilities agent,
        LlmAgentNativeRuntimeOptions runtime,
        long retentionBytes)
    {
        LlmAgentRuntimePolicy policy = runtime.Policy
            ?? throw new ArgumentException("Native runtime options require a runtime policy.", nameof(runtime));
        if (!agent.SupportedRuntimePolicies.Contains(policy.Kind))
        {
            throw new NotSupportedException(
                $"This native runtime does not accept a {policy.Kind} runtime policy; check SupportedRuntimePolicies.");
        }
        AgentResourceOptions.Validate(
            agent,
            policy is LlmAgentHostRuntimePolicy { AllowRuntimeBackgroundWork: true },
            retentionBytes);
    }

    /// <summary>
    /// Fails when the connection's overrides disable the operation. <paramref name="supportsAgentToolUse"/> is
    /// what the runtime implements; overrides can only lower it.
    /// </summary>
    public static void ValidateConnection(
        LlmConnection connection,
        LlmAgentCapabilities agent,
        bool supportsAgentToolUse,
        string? modelId,
        IReadOnlyList<LlmAgentToolDefinition> tools,
        LlmAgentToolHandler? toolHandler)
    {
        var implemented = new LlmProviderCapabilities
        {
            SupportsAgentSessions = true,
            SupportsAgentToolUse = supportsAgentToolUse,
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

    /// <summary>A continuation stream that reports <see cref="UnsupportedContext"/> on its first read.</summary>
    public static async IAsyncEnumerable<LlmAgentEvent> UnsupportedContinuation(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.FromException(UnsupportedContext(cancellationToken));
        yield break;
    }
}
