namespace Darkspyre.Inference.Connect.LLM.Internal;

/// <summary>
/// A chat client whose model service compacts a conversation itself. A managed session uses it in place of
/// <see cref="ConversationCompactor"/> for the models it reports, and falls back to that compactor when a call fails.
/// </summary>
internal interface ILlmProviderCompaction
{
    /// <summary>True when the model service compacts this model's conversations.</summary>
    bool CompactsProviderSide(LlmConnection connection, string modelId);

    /// <summary>
    /// Sends <paramref name="request"/> to the model service's compaction and returns the compacted context. Its entries
    /// replace every entry of the context the request was built from.
    /// </summary>
    Task<LlmAgentContext> CompactAsync(LlmConnection connection, LlmChatRequest request, CancellationToken cancellationToken);
}
