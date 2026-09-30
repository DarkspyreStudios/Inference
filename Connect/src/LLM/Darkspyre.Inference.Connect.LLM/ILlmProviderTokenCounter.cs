namespace Darkspyre.Inference.Connect.LLM;

/// <summary>An optional adapter capability for counting a projected input through the provider's count API.</summary>
public interface ILlmProviderTokenCounter
{
    Task<LlmProviderInputTokenCount> CountInputTokensAsync(
        LlmConnection connection,
        LlmChatRequest request,
        CancellationToken cancellationToken = default);
}

public enum LlmTokenModality
{
    Unknown,
    Text,
    Image,
    Audio,
    Video,
    Document
}

/// <summary>A provider-reported input breakdown. Unknown retains the count of an unrecognized modality.</summary>
public sealed record LlmTokenCountDetail(LlmTokenModality Modality, long Tokens);

/// <summary>
/// A provider count for one resolved request, not generated usage, a billing record or proof of admission.
/// Providers can report a preflight count that differs from actual generation usage. No local estimate is substituted.
/// </summary>
public sealed record LlmProviderInputTokenCount
{
    public required string ModelId { get; init; }
    public required long InputTokens { get; init; }
    /// <summary>Null means not reported, not zero.</summary>
    public long? CachedInputTokens { get; init; }
    public long? BillableCharacters { get; init; }
    public IReadOnlyList<LlmTokenCountDetail> InputTokenDetails { get; init; } = [];
    public IReadOnlyList<LlmTokenCountDetail> CacheTokenDetails { get; init; } = [];
    public int OmittedContinuationStateCount { get; init; }
}
