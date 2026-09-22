using TensorSharp;
using TensorSharp.Models.Direct;
using TensorSharp.Runtime;
using TsTensor = TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.Image.Supra;

internal readonly record struct SupraTextCondition(float[] HiddenStates, float[] AttentionMask, int TokenCount);

internal sealed class FlanT5Encoder : IDisposable
{
    public const int HiddenSize = 768;
    private const int LayerCount = 12;
    private const int AttentionHeads = 12;
    private const int HeadDimension = 64;
    private const int RelativeBuckets = 32;
    private const int RelativeMaximumDistance = 128;
    private const float NormEpsilon = 1e-6f;

    private readonly DirectContext _context;
    private readonly DirectEmbedding _embedding;
    private readonly EncoderLayer[] _layers;
    private readonly TsTensor _finalNorm;
    private readonly float[] _relativeBias;
    private bool _disposed;

    public FlanT5Encoder(DirectContext context, IFloatTensorStore weights)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(weights);
        _context = context;
        try
        {
            _embedding = DirectEmbedding.FromTensorStore(context, weights, "shared.weight");
            _layers = Enumerable.Range(0, LayerCount)
                .Select(index => new EncoderLayer(context, weights, index))
                .ToArray();
            _finalNorm = LoadVector(weights, "encoder.final_layer_norm.weight", HiddenSize);
            _relativeBias = weights.ReadFloat32(
                "encoder.block.0.layer.0.SelfAttention.relative_attention_bias.weight");
            if (_relativeBias.Length != RelativeBuckets * AttentionHeads)
            {
                throw new InvalidDataException("Flan-T5 relative attention bias has an unexpected shape.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public SupraTextCondition Encode(SupraTextTokenizer tokenizer, string prompt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(prompt);
        int[] tokenIds = tokenizer.Encode(prompt);
        TsTensor? hidden = null;
        try
        {
            hidden = _embedding.Forward(tokenIds);
            using TsTensor attentionBias = BuildAttentionBias(tokenIds.Length);
            foreach (EncoderLayer layer in _layers)
            {
                layer.Forward(hidden, attentionBias);
            }

            hidden = Replace(hidden, DirectOps.RmsNorm(_context, hidden, _finalNorm, NormEpsilon));
            float[] encoded = DirectOps.ToArray(hidden);
            var fixedHidden = new float[SupraTextTokenizer.MaximumLength * HiddenSize];
            encoded.CopyTo(fixedHidden, 0);
            var mask = new float[SupraTextTokenizer.MaximumLength];
            Array.Fill(mask, 1f, 0, tokenIds.Length);
            return new SupraTextCondition(fixedHidden, mask, tokenIds.Length);
        }
        finally
        {
            hidden?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _embedding?.Dispose();
        if (_layers is not null)
        {
            foreach (EncoderLayer layer in _layers)
            {
                layer.Dispose();
            }
        }
    }

    private TsTensor BuildAttentionBias(int tokenCount)
    {
        var values = new float[AttentionHeads * tokenCount * tokenCount];
        for (int head = 0; head < AttentionHeads; head++)
        {
            int headOffset = head * tokenCount * tokenCount;
            for (int query = 0; query < tokenCount; query++)
            {
                for (int key = 0; key < tokenCount; key++)
                {
                    int bucket = RelativePositionBucket(key - query);
                    values[headOffset + query * tokenCount + key] =
                        _relativeBias[bucket * AttentionHeads + head];
                }
            }
        }

        return _context.FromFloats(values, AttentionHeads, tokenCount, tokenCount);
    }

    internal static int RelativePositionBucket(int relativePosition)
    {
        int halfBuckets = RelativeBuckets / 2;
        int bucket = relativePosition > 0 ? halfBuckets : 0;
        int distance = Math.Abs(relativePosition);
        int exactBuckets = halfBuckets / 2;
        if (distance < exactBuckets)
        {
            return bucket + distance;
        }

        double scaled = Math.Log((double)distance / exactBuckets)
            / Math.Log((double)RelativeMaximumDistance / exactBuckets);
        int logarithmic = exactBuckets + (int)(scaled * (halfBuckets - exactBuckets));
        return bucket + Math.Min(logarithmic, halfBuckets - 1);
    }

    private TsTensor LoadVector(IFloatTensorStore weights, string name, int length)
    {
        long[] shape = weights.TensorShape(name);
        if (shape.Length != 1 || shape[0] != length)
        {
            throw new InvalidDataException($"Tensor '{name}' must have shape [{length}].");
        }

        return _context.Own(_context.FromFloats(weights.ReadFloat32(name), length));
    }

    private static TsTensor Replace(TsTensor previous, TsTensor replacement)
    {
        previous.Dispose();
        return replacement;
    }

    private sealed class EncoderLayer : IDisposable
    {
        private readonly DirectContext _context;
        private readonly TsTensor _attentionNorm;
        private readonly TsTensor _feedForwardNorm;
        private readonly DirectLinear _query;
        private readonly DirectLinear _key;
        private readonly DirectLinear _value;
        private readonly DirectLinear _attentionOutput;
        private readonly DirectLinear _gate;
        private readonly DirectLinear _up;
        private readonly DirectLinear _down;

        internal EncoderLayer(DirectContext context, IFloatTensorStore weights, int index)
        {
            _context = context;
            string attention = $"encoder.block.{index}.layer.0";
            string feedForward = $"encoder.block.{index}.layer.1";
            _attentionNorm = OwnVector(context, weights, $"{attention}.layer_norm.weight");
            _feedForwardNorm = OwnVector(context, weights, $"{feedForward}.layer_norm.weight");
            _query = DirectLinear.FromTensorStore(context, weights, $"{attention}.SelfAttention.q.weight");
            _key = DirectLinear.FromTensorStore(context, weights, $"{attention}.SelfAttention.k.weight");
            _value = DirectLinear.FromTensorStore(context, weights, $"{attention}.SelfAttention.v.weight");
            _attentionOutput = DirectLinear.FromTensorStore(context, weights, $"{attention}.SelfAttention.o.weight");
            _gate = DirectLinear.FromTensorStore(context, weights, $"{feedForward}.DenseReluDense.wi_0.weight");
            _up = DirectLinear.FromTensorStore(context, weights, $"{feedForward}.DenseReluDense.wi_1.weight");
            _down = DirectLinear.FromTensorStore(context, weights, $"{feedForward}.DenseReluDense.wo.weight");
        }

        internal void Forward(TsTensor input, TsTensor attentionBias)
        {
            using (TsTensor normalized = DirectOps.RmsNorm(_context, input, _attentionNorm, NormEpsilon))
            using (TsTensor query = _query.Forward(normalized))
            using (TsTensor key = _key.Forward(normalized))
            using (TsTensor value = _value.Forward(normalized))
            using (TsTensor attention = DirectOps.Attention(
                _context,
                query,
                key,
                value,
                AttentionHeads,
                HeadDimension,
                1f,
                attentionBias))
            using (TsTensor projected = _attentionOutput.Forward(attention))
            {
                Ops.Add(input, input, projected);
            }

            using (TsTensor normalized = DirectOps.RmsNorm(_context, input, _feedForwardNorm, NormEpsilon))
            using (TsTensor gate = _gate.Forward(normalized))
            using (TsTensor activated = DirectOps.Gelu(_context, gate))
            using (TsTensor up = _up.Forward(normalized))
            {
                Ops.Mul(activated, activated, up);
                using TsTensor projected = _down.Forward(activated);
                Ops.Add(input, input, projected);
            }
        }

        public void Dispose()
        {
            _query.Dispose();
            _key.Dispose();
            _value.Dispose();
            _attentionOutput.Dispose();
            _gate.Dispose();
            _up.Dispose();
            _down.Dispose();
        }

        private static TsTensor OwnVector(
            DirectContext context,
            IFloatTensorStore weights,
            string name)
        {
            long[] shape = weights.TensorShape(name);
            if (shape.Length != 1 || shape[0] != HiddenSize)
            {
                throw new InvalidDataException($"Tensor '{name}' must have shape [{HiddenSize}].");
            }

            return context.Own(context.FromFloats(weights.ReadFloat32(name), HiddenSize));
        }
    }
}
