namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Tokenizes one plain string through the selected provider. This is not a resolved-request counter.</summary>
public interface ILlmTextTokenizer
{
    Task<LlmTokenization> TokenizeAsync(string connectionId, string modelId, string text,
        CancellationToken cancellationToken = default);
}

/// <summary>The provider's ordered tokens. This result does not report request occupancy, usage or billing.</summary>
public sealed record LlmTokenization(IReadOnlyList<LlmToken> Tokens);

/// <summary>A provider token with its exact display text and copied bytes. Bytes need not decode independently as UTF-8.</summary>
public sealed record LlmToken(uint Id, string Text, ReadOnlyMemory<byte> Bytes);
