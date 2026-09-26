using Microsoft.ML.Tokenizers;
using MlSentencePieceTokenizer = Microsoft.ML.Tokenizers.SentencePieceTokenizer;

namespace Darkspyre.Inference.Tensor.Image.Supra;

internal sealed class SupraTextTokenizer
{
    public const int MaximumLength = 128;
    public const int EndOfSentenceId = 1;

    private readonly MlSentencePieceTokenizer _tokenizer;

    private SupraTextTokenizer(MlSentencePieceTokenizer tokenizer)
    {
        _tokenizer = tokenizer;
    }

    /// <summary>Loads the SentencePiece model at <paramref name="path"/>. ML.Tokenizers reads it synchronously.</summary>
    public static SupraTextTokenizer Create(string path)
    {
        using FileStream content = File.OpenRead(path);
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
