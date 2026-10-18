using TensorSharp;
using TensorSharp.Runtime;
using TsTensor = TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.Image.Supra.Direct;

/// <summary>
/// A row-major <c>[vocabulary, embedding]</c> token table on the context's allocator; token ids are gathered with
/// <c>Ops.IndexSelect</c>. Ported from the TensorSharp fork's <c>DirectEmbedding</c>.
/// </summary>
internal sealed class DirectEmbedding : IDisposable
{
    private readonly DirectContext _context;
    private readonly TsTensor _weight;
    private readonly long _vocabularySize;
    private readonly long _embeddingSize;

    private DirectEmbedding(DirectContext context, TsTensor weight, long vocabularySize, long embeddingSize)
    {
        _context = context;
        _weight = weight;
        _vocabularySize = vocabularySize;
        _embeddingSize = embeddingSize;
    }

    /// <summary>Loads a row-major <c>[vocabulary, embedding]</c> tensor from a tensor store.</summary>
    public static DirectEmbedding FromTensorStore(DirectContext context, IFloatTensorStore store, string weight)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(weight);
        long[] shape = store.TensorShape(weight);
        if (shape.Length != 2 || shape[0] <= 0 || shape[1] <= 0)
        {
            throw new InvalidOperationException(
                $"Direct embedding weight '{weight}' must have shape [vocabulary, embedding].");
        }

        float[] values = store.ReadFloat32(weight);
        if (values.LongLength != checked(shape[0] * shape[1]))
        {
            throw new ArgumentException(
                $"Embedding weight length {values.LongLength} does not match [{shape[0]}, {shape[1]}].", nameof(weight));
        }

        return new DirectEmbedding(context, context.FromFloats(values, shape[0], shape[1]), shape[0], shape[1]);
    }

    /// <summary>Gathers the token ids into a new <c>[tokens, embedding]</c> F32 tensor.</summary>
    public TsTensor Forward(ReadOnlySpan<int> tokenIds)
    {
        if (tokenIds.Length == 0)
        {
            throw new ArgumentException("At least one token ID is required.", nameof(tokenIds));
        }

        int[] ids = tokenIds.ToArray();
        for (int index = 0; index < ids.Length; index++)
        {
            if ((uint)ids[index] >= (ulong)_vocabularySize)
            {
                throw new ArgumentOutOfRangeException(nameof(tokenIds),
                    $"Token ID {ids[index]} at index {index} is outside [0, {_vocabularySize}).");
            }
        }

        using var indices = new TsTensor(_context.Allocator, DType.Int32, ids.LongLength);
        indices.SetElementsAsInt(ids);
        TsTensor result = _context.NewF32(ids.LongLength, _embeddingSize);
        try
        {
            Ops.IndexSelect(result, _weight, indices);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public void Dispose() => _weight.Dispose();
}
