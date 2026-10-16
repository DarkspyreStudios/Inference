namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Tokenizes one plain string through the selected provider. This is not a resolved-request counter.</summary>
public interface ILlmTextTokenizer
{
    /// <summary>Tokenizes <paramref name="text"/> with the model's tokenizer through the connection.</summary>
    Task<LlmTokenization> TokenizeAsync(string connectionId, string modelId, string text,
        CancellationToken cancellationToken = default);
}

/// <summary>The provider's ordered tokens. This result does not report request occupancy, usage or billing.</summary>
/// <param name="Tokens">The tokens, in order.</param>
public sealed record LlmTokenization(IReadOnlyList<LlmToken> Tokens);

/// <summary>A provider token with its exact text when available and copied bytes. Bytes need not decode independently as UTF-8.</summary>
/// <param name="Id">The provider's token id.</param>
/// <param name="Text">The token's exact text, or null when the provider returns a byte-only piece.</param>
/// <param name="Bytes">A copy of the token's bytes.</param>
public sealed record LlmToken(uint Id, string? Text, ReadOnlyMemory<byte> Bytes);
