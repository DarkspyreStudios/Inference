using System.Runtime.CompilerServices;

namespace Darkspyre.Inference.Connect.LLM.Examples;

/// <summary>
/// Host integration example for the managed Codex runtime. Native adapters fail these capability
/// checks. Persistence and summarization remain in the consuming application's existing services.
/// </summary>
public static class HostOwnedAgentContext
{
    /// <summary>Open restored context; use a new thread ID with a copied prefix to fork.</summary>
    public static async Task<ILlmAgentSession> OpenAsync(
        LlmProviderService providers,
        LlmAgentProviderService agents,
        string connectionId,
        LlmAgentSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(options);
        var capabilities = await providers.GetModelCapabilitiesAsync(connectionId, options.ModelId, cancellationToken);
        if (!capabilities.SupportsAgentSessions
            || capabilities.Agent.ContextOwnership != LlmAgentContextOwnership.Host
            || !capabilities.Agent.SupportsContextCapture
            || !capabilities.Agent.SupportsContextReplacement)
        {
            throw new NotSupportedException("This runtime cannot open a host-owned conversation snapshot.");
        }

        return await agents.StartSessionAsync(connectionId, options, cancellationToken);
    }

    /// <summary>Call only after consuming the paused enumeration completely. Never strip live bytes.</summary>
    public static async Task<LlmAgentContext> CaptureForCompactionAsync(
        ILlmAgentSession session,
        LlmAgentCompactionRequiredEvent pause,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pause);
        var capabilities = await session.GetCapabilitiesAsync(cancellationToken);
        if (!capabilities.SupportsCompactionHandoff || !capabilities.SupportsContextCapture)
        {
            throw new NotSupportedException("This runtime does not support host compaction handoff.");
        }
        if (pause.ThreadId != session.ThreadId || !pause.CanCompactHistory)
        {
            throw new InvalidOperationException("This pause cannot be resolved by compacting this session's history.");
        }

        return await session.CaptureContextAsync(new() { IncludeAttachmentData = true }, cancellationToken);
    }

    /// <summary>
    /// The host has summarized only unprotected complete groups and persisted its chosen projection.
    /// It must retain the original durable context if validation fails. The runtime validates and
    /// atomically installs replacement; it ignores replacement.Revision when assigning the next revision.
    /// </summary>
    public static async IAsyncEnumerable<LlmAgentEvent> ContinueAfterCompactionAsync(
        ILlmAgentSession session,
        LlmAgentCompactionRequiredEvent pause,
        LlmAgentContext captured,
        LlmAgentContext replacement,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pause);
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(replacement);
        if (pause.ThreadId != session.ThreadId)
        {
            throw new InvalidOperationException("Compaction must target the captured pause and revision.");
        }

        var continuation = new LlmAgentTurnContinuation(
            pause.TurnId, pause.PauseId, captured.Revision, replacement);
        await foreach (var item in session.ContinueTurnAsync(continuation, cancellationToken))
        {
            yield return item;
        }
    }
}
