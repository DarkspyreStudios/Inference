using Microsoft.ML.Tokenizers;
using TensorSharp.Runtime;
using MlSentencePieceTokenizer = Microsoft.ML.Tokenizers.SentencePieceTokenizer;

namespace Darkspyre.Inference.Tensor.Image;

internal sealed class SupraTextTokenizer
{
    public const int MaximumLength = 128;
    public const int EndOfSentenceId = 1;

    private readonly MlSentencePieceTokenizer _tokenizer;

    private SupraTextTokenizer(MlSentencePieceTokenizer tokenizer)
    {
        _tokenizer = tokenizer;
    }

    public static async Task<SupraTextTokenizer> CreateAsync(
        PersistenceFileReference source,
        CancellationToken cancellationToken)
    {
        await using Stream? content = await source.Store.OpenReadAsync(
            source.StoreName,
            source.AssetName,
            cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            throw new FileNotFoundException($"Tokenizer artifact '{source}' does not exist.", source.AssetName);
        }

        return new SupraTextTokenizer(MlSentencePieceTokenizer.Create(
            content,
            addBeginningOfSentence: false,
            addEndOfSentence: false));
    }

    public int[] Encode(string prompt)
    {
        IReadOnlyList<int> encoded = _tokenizer.EncodeToIds(
            prompt,
            addBeginningOfSentence: false,
            addEndOfSentence: false,
            considerPreTokenization: true,
            considerNormalization: true);
        int count = Math.Min(encoded.Count, MaximumLength - 1);
        var result = new int[count + 1];
        for (int index = 0; index < count; index++)
        {
            result[index] = encoded[index];
        }

        result[^1] = EndOfSentenceId;
        return result;
    }
}
