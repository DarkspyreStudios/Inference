namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Tokenizes one plain string through the selected provider. This is not a resolved-request counter. No phase-1 provider implements this; it is a forward seam under the L2 exception in Inference/AGENTS.md, tracked by DSA-121.</summary>
public interface ILlmTextTokenizer
{
    /// <summary>Tokenizes <paramref name="text"/> with the model's tokenizer through the connection.</summary>
    Task<LlmTokenization> TokenizeAsync(string connectionId, string modelId, string text,
        CancellationToken cancellationToken = default);
}

/// <summary>The provider's ordered tokens. This result does not report request occupancy, usage or billing.</summary>
public sealed record LlmTokenization(IReadOnlyList<LlmToken> Tokens);

/// <summary>A provider token with its exact display text and copied bytes. Bytes need not decode independently as UTF-8.</summary>
public sealed record LlmToken(uint Id, string Text, ReadOnlyMemory<byte> Bytes);
