namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Read access to the Host's full transcript of one conversation.</summary>
public interface ILlmConversationTranscript
{
    /// <summary>
    /// Whole turns of real messages after <paramref name="cursor"/>, oldest first: at least one turn, and more than
    /// <paramref name="limit"/> entries only when one turn is larger. A null cursor starts at the oldest message. Each
    /// entry's TurnId is the turn id the conversation reported for that turn in <see cref="LlmAgentTurnStartedEvent"/>,
    /// or the TurnId the entry carried in a supplied context.
    /// </summary>
    /// <gotcha>Entries are real messages only: never a compaction summary and never provider continuation state.</gotcha>
    Task<LlmTranscriptPage> ReadAsync(string? cursor, int limit, CancellationToken cancellationToken = default);
}

/// <summary>One page of transcript entries; NextCursor is null after the newest entry.</summary>
public sealed record LlmTranscriptPage(IReadOnlyList<LlmAgentContextEntry> Entries, string? NextCursor);
