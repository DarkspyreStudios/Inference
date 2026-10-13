using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>Current native-runtime declarations and fail-fast managed-only option checks.</summary>
internal static class NativeAgentContract
{
    // Declared before the capability records that read it: static initializers run in textual order.
    private static IReadOnlySet<LlmAgentRuntimePolicyKind> HostControlled { get; } =
        FrozenSet.Create(LlmAgentRuntimePolicyKind.HostControlled);

    public static LlmAgentCapabilities Claude { get; } = new()
    {
        ContextImport = LlmAgentContextImport.OpeningContext,
        SupportsShellTools = InferenceSupport.Unsupported,
        SupportsContextCapture = InferenceSupport.Unsupported,
        SupportsContextReplacement = InferenceSupport.Unsupported,
        // ClaudeConversation measures its own saved history.
        SupportsContextStatus = InferenceSupport.Supported,
        SupportsCompactionHandoff = InferenceSupport.Unsupported,
        SupportsNativeResume = InferenceSupport.Unsupported,
        SupportsSteering = InferenceSupport.Unsupported,
        SupportsInterrupt = InferenceSupport.Supported,
        SupportsExternalToolHandoff = InferenceSupport.Unknown,
        SupportsCompactNow = InferenceSupport.Supported,
        SupportsFallbackModels = InferenceSupport.Unknown,
        SupportsTurnCap = InferenceSupport.Unknown,
        SupportsSpendCap = InferenceSupport.Unknown,
        RichContentSupported = RichContentRoutes.ClaudeMcp,
        SupportedRuntimePolicies = HostControlled,
        ResourceObservation = LlmOperationSupport.Supported
    };

    /// <summary>
    /// Fails when the connection's overrides disable the operation. <paramref name="supportsAgentToolUse"/> is
    /// what the runtime implements; overrides can only lower it.
    /// </summary>
    public static void ValidateConnection(
        LlmConnection connection,
        LlmAgentCapabilities agent,
        bool supportsAgentToolUse,
        string? modelId,
        IReadOnlyList<LlmAgentClientToolDefinition> tools,
        LlmAgentToolHandler? toolHandler)
    {
        var implemented = new LlmProviderCapabilities
        {
            SupportsAgentSessions = InferenceSupport.Supported,
            SupportsAgentToolUse = (supportsAgentToolUse ? InferenceSupport.Supported : InferenceSupport.Unsupported),
            Agent = agent
        };
        var effective = implemented.Apply(connection.CapabilityOverrides);
        if (modelId is not null && connection.ModelCapabilityOverrides.TryGetValue(modelId, out var modelOverrides))
        {
            effective = effective.Apply(modelOverrides);
        }
        if (effective.SupportsAgentSessions == InferenceSupport.Unsupported)
            throw new NotSupportedException(AgentSessionsUnsupported);
        if ((effective.SupportsAgentToolUse == InferenceSupport.Unsupported) && (tools.Count > 0 || toolHandler is not null))
            throw new NotSupportedException(AgentToolUseUnsupported);
    }

    /// <summary>The refusal of an opening on a model that marks agent sessions Unsupported.</summary>
    public const string AgentSessionsUnsupported = "The selected model does not support agent sessions.";

    /// <summary>The refusal of an opening with tools on a model that marks agent tool use Unsupported.</summary>
    public const string AgentToolUseUnsupported = "The selected model does not support agent tool use.";

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
