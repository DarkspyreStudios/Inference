using Darkspyre.Inference.Common;
using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>How a session start takes a host-supplied context.</summary>
public enum LlmAgentContextImport
{
    /// <summary>A supplied context is rejected.</summary>
    None,

    /// <summary>The entries become the session's host-owned context.</summary>
    Structured,

    /// <summary>
    /// A new provider-side conversation starts with the entries as a text transcript in its first turn.
    /// No continuation state is carried.
    /// </summary>
    OpeningContext
}

/// <summary>
/// Operations implemented by the selected agent runtime. Unsupported methods fail before effects.
/// Native runtime memory is not equivalent to a host-owned, exportable conversation.
/// </summary>
public sealed record LlmAgentCapabilities
{
    public IReadOnlySet<LlmHostedToolKind> HostedTools { get; init => field = value.ToFrozenSet(); } = FrozenSet<LlmHostedToolKind>.Empty;

    /// <summary>The agent lane routes shell calls to the borrowed caller executor.</summary>
    public InferenceSupport SupportsShellTools { get; init; }

    /// <summary>How a session start accepts <see cref="LlmAgentSessionOptions.Context"/>.</summary>
    public LlmAgentContextImport ContextImport { get; init; }

    public InferenceSupport SupportsContextCapture { get; init; }

    public InferenceSupport SupportsContextReplacement { get; init; }

    public InferenceSupport SupportsContextStatus { get; init; }

    public InferenceSupport SupportsCompactionHandoff { get; init; }

    /// <summary>Creates a provider compaction proposal; the host still commits the replacement.</summary>
    public InferenceSupport SupportsProviderCompaction { get; init; }

    public InferenceSupport SupportsNativeResume { get; init; }

    /// <summary>SteerAsync adds input to a running turn.</summary>
    public InferenceSupport SupportsSteering { get; init; }

    /// <summary>Explicit InterruptAsync support; enumeration cancellation is separate.</summary>
    public InferenceSupport SupportsInterrupt { get; init; }

    /// <summary>LlmAgentToolHandling.ReturnRequests ends a turn with the model's tool calls returned unrun.</summary>
    public InferenceSupport SupportsExternalToolHandoff { get; init; }

    /// <summary>CompactNowAsync compacts this conversation.</summary>
    public InferenceSupport SupportsCompactNow { get; init; }

    /// <summary>FallbackModelIds is honoured.</summary>
    public InferenceSupport SupportsFallbackModels { get; init; }

    /// <summary>LlmAgentExecutionOptions.MaxModelIterations ends a turn that reaches it.</summary>
    public InferenceSupport SupportsTurnCap { get; init; }

    /// <summary>LlmAgentExecutionOptions.MaxSpendUsd ends a turn that reaches it.</summary>
    public InferenceSupport SupportsSpendCap { get; init; }

    /// <summary>Whether the session reports the runtime's own background work through its resource members.</summary>
    public LlmOperationSupport ResourceObservation { get; init; } = LlmOperationSupport.Unsupported;

    /// <summary>How host tool output reaches the model in this runtime's sessions.</summary>
    internal LlmRichContentSupported RichContentSupported { get; init; } = new();

    /// <summary>
    /// The <see cref="LlmAgentNativeRuntimeOptions.Policy"/> variants a native runtime accepts. Managed
    /// runtimes accept no native runtime options and report an empty set.
    /// </summary>
    public IReadOnlySet<LlmAgentRuntimePolicyKind> SupportedRuntimePolicies { get; init; } = NoRuntimePolicies;

    internal static IReadOnlySet<LlmAgentRuntimePolicyKind> NoRuntimePolicies { get; } =
        FrozenSet<LlmAgentRuntimePolicyKind>.Empty;

    /// <summary>True when no agent operation is declared.</summary>
    internal bool IsNone => SupportedRuntimePolicies.Count == 0 && HostedTools.Count == 0
        && this with
        {
            SupportedRuntimePolicies = NoRuntimePolicies,
            HostedTools = FrozenSet<LlmHostedToolKind>.Empty
        } == new LlmAgentCapabilities();

    internal void Validate()
    {
        if (!Enum.IsDefined(ContextImport)
            || SupportedRuntimePolicies is null || SupportedRuntimePolicies.Any(kind => !Enum.IsDefined(kind))
            || HostedTools.Any(kind => !Enum.IsDefined(kind))
            || ((SupportsCompactionHandoff == InferenceSupport.Supported)
                && ((SupportsContextCapture == InferenceSupport.Unsupported) || (SupportsContextReplacement == InferenceSupport.Unsupported) || (SupportsContextStatus == InferenceSupport.Unsupported))))
        {
            throw new ArgumentException("The agent capability combination cannot be implemented consistently.");
        }
    }
}
