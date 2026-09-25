namespace Darkspyre.Inference.Connect.LLM;

public enum LlmAgentContextOwnership
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
    /// No response state is carried.
    /// </summary>
    OpeningContext
}

public enum LlmAgentCompactionOwnership
{
    None,
    Host,
    Provider
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
    public LlmAgentContextOwnership ContextOwnership { get; init; }

    /// <summary>How a session start accepts <see cref="LlmAgentSessionOptions.Context"/>.</summary>
    public LlmAgentContextImport ContextImport { get; init; }

    public bool SupportsContextCapture { get; init; }

    public bool SupportsContextReplacement { get; init; }

    public bool SupportsContextStatus { get; init; }

    public bool SupportsCompactionHandoff { get; init; }

    public bool SupportsNativeResume { get; init; }

    public LlmAgentNativeFork NativeFork { get; init; }

    public LlmAgentCompactionOwnership CompactionOwnership { get; init; }

    public LlmAgentSteering Steering { get; init; }

    /// <summary>Explicit InterruptAsync support; enumeration cancellation is separate.</summary>
    public bool SupportsInterrupt { get; init; }

    /// <summary>Whether the session reports the runtime's own background work through its resource members.</summary>
    public LlmOperationSupport ResourceObservation { get; init; } = LlmOperationSupport.Unsupported;

    public LlmAgentToolExecutionOwnership ToolExecutionOwnership { get; init; }

    /// <summary>How host tool output reaches the model in this runtime's sessions.</summary>
    public LlmRichContentSupported RichContentSupported { get; init; } = new();

    internal void Validate()
    {
        if (!Enum.IsDefined(ContextOwnership) || !Enum.IsDefined(ContextImport) || !Enum.IsDefined(CompactionOwnership)
            || !Enum.IsDefined(NativeFork) || !Enum.IsDefined(Steering)
            || !Enum.IsDefined(ToolExecutionOwnership)
            || (ContextOwnership == LlmAgentContextOwnership.None && this != new LlmAgentCapabilities())
            || (SupportsCompactionHandoff && (ContextOwnership != LlmAgentContextOwnership.Host
                || CompactionOwnership != LlmAgentCompactionOwnership.Host
                || !SupportsContextCapture || !SupportsContextReplacement || !SupportsContextStatus))
            || (NativeFork != LlmAgentNativeFork.None && !SupportsNativeResume)
            || (ContextImport == LlmAgentContextImport.Structured && ContextOwnership != LlmAgentContextOwnership.Host)
            || (ContextImport == LlmAgentContextImport.OpeningContext && ContextOwnership != LlmAgentContextOwnership.Provider)
            || (ContextOwnership == LlmAgentContextOwnership.Host
                && (SupportsNativeResume || NativeFork != LlmAgentNativeFork.None)))
        {
            throw new ArgumentException("The agent capability combination cannot be implemented consistently.");
        }
    }
}
