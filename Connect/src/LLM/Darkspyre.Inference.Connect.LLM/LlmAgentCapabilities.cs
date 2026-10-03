using System.Collections.Frozen;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Who owns a part of the session: nobody, the host, or the provider-owned runtime.</summary>
public enum LlmAgentOwnership
{
    None,
    Host,
    Provider
}

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

public enum LlmAgentNativeFork
{
    None,
    CurrentPosition
}

public enum LlmAgentSteering
{
    None,
    SafeBoundary
}

public enum LlmAgentToolExecutionOwnership
{
    None,
    Host,
    Provider,
    Mixed
}

/// <summary>
/// Operations implemented by the selected agent runtime. Unsupported methods fail before effects.
/// Native runtime memory is not equivalent to a host-owned, exportable conversation.
/// </summary>
public sealed record LlmAgentCapabilities
{
    public IReadOnlySet<LlmHostedToolKind> HostedTools { get; init => field = value.ToFrozenSet(); } = FrozenSet<LlmHostedToolKind>.Empty;

    /// <summary>The agent lane routes shell calls to the borrowed caller executor.</summary>
    public bool SupportsShellTools { get; init; }

    /// <summary>Caller services actually mapped by this agent lane.</summary>
    public IReadOnlySet<LlmAgentCallerServiceKind> SupportedCallerServices
    {
        get;
        init => field = value.ToFrozenSet();
    } = FrozenSet<LlmAgentCallerServiceKind>.Empty;

    public LlmAgentOwnership ContextOwnership { get; init; }

    /// <summary>How a session start accepts <see cref="LlmAgentSessionOptions.Context"/>.</summary>
    public LlmAgentContextImport ContextImport { get; init; }

    public bool SupportsContextCapture { get; init; }

    public bool SupportsContextReplacement { get; init; }

    public bool SupportsContextStatus { get; init; }

    public bool SupportsCompactionHandoff { get; init; }

    /// <summary>Creates a provider compaction proposal; the host still commits the replacement.</summary>
    public bool SupportsProviderCompaction { get; init; }

    public bool SupportsNativeResume { get; init; }

    public LlmAgentNativeFork NativeFork { get; init; }

    public LlmAgentOwnership CompactionOwnership { get; init; }

    public LlmAgentSteering Steering { get; init; }

    /// <summary>Explicit InterruptAsync support; enumeration cancellation is separate.</summary>
    public bool SupportsInterrupt { get; init; }

    /// <summary>Whether the session reports the runtime's own background work through its resource members.</summary>
    public LlmOperationSupport ResourceObservation { get; init; } = LlmOperationSupport.Unsupported;

    public LlmAgentToolExecutionOwnership ToolExecutionOwnership { get; init; }

    /// <summary>How host tool output reaches the model in this runtime's sessions.</summary>
    public LlmRichContentSupported RichContentSupported { get; init; } = new();

    /// <summary>
    /// The <see cref="LlmAgentNativeRuntimeOptions.Policy"/> variants a native runtime accepts. Managed
    /// runtimes accept no native runtime options and report an empty set.
    /// </summary>
    public IReadOnlySet<LlmAgentRuntimePolicyKind> SupportedRuntimePolicies { get; init; } = NoRuntimePolicies;

    internal static IReadOnlySet<LlmAgentRuntimePolicyKind> NoRuntimePolicies { get; } =
        FrozenSet<LlmAgentRuntimePolicyKind>.Empty;

    /// <summary>True when no agent operation is declared.</summary>
    internal bool IsNone => SupportedRuntimePolicies.Count == 0 && HostedTools.Count == 0 && SupportedCallerServices.Count == 0
        && this with
        {
            SupportedRuntimePolicies = NoRuntimePolicies,
            HostedTools = FrozenSet<LlmHostedToolKind>.Empty,
            SupportedCallerServices = FrozenSet<LlmAgentCallerServiceKind>.Empty
        } == new LlmAgentCapabilities();

    internal void Validate()
    {
        if (!Enum.IsDefined(ContextOwnership) || !Enum.IsDefined(ContextImport) || !Enum.IsDefined(CompactionOwnership)
            || !Enum.IsDefined(NativeFork) || !Enum.IsDefined(Steering)
            || !Enum.IsDefined(ToolExecutionOwnership)
            || SupportedRuntimePolicies is null || SupportedRuntimePolicies.Any(kind => !Enum.IsDefined(kind))
            || HostedTools.Any(kind => !Enum.IsDefined(kind))
            || SupportedCallerServices.Any(kind => !Enum.IsDefined(kind))
            || (ContextOwnership == LlmAgentOwnership.None && !IsNone)
            || (ContextOwnership == LlmAgentOwnership.Host && SupportedRuntimePolicies.Count > 0)
            || (SupportsCompactionHandoff && (ContextOwnership != LlmAgentOwnership.Host
                || CompactionOwnership != LlmAgentOwnership.Host
                || !SupportsContextCapture || !SupportsContextReplacement || !SupportsContextStatus))
            || (SupportsProviderCompaction && (ContextOwnership != LlmAgentOwnership.Host
                || !SupportsCompactionHandoff))
            || (NativeFork != LlmAgentNativeFork.None && !SupportsNativeResume)
            || (ContextImport == LlmAgentContextImport.Structured && ContextOwnership != LlmAgentOwnership.Host)
            || (ContextImport == LlmAgentContextImport.OpeningContext && ContextOwnership != LlmAgentOwnership.Provider)
            || (ContextOwnership == LlmAgentOwnership.Host
                && (SupportsNativeResume || NativeFork != LlmAgentNativeFork.None)))
        {
            throw new ArgumentException("The agent capability combination cannot be implemented consistently.");
        }
    }
}
