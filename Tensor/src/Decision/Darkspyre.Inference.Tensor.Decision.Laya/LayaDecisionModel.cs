using TensorSharp;
using TensorSharp.Cpu;
using TensorSharp.Cuda;
using TensorSharp.Models.Direct;
using TensorSharp.Runtime;
using TsTensor = global::TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.Decision.Laya;

internal readonly record struct LayaModelOutput(float[] Logits, float ActionProbability);

internal sealed class LayaDecisionModel : IDisposable
{
    private const int HiddenSize = 1_024;
    private const int IntermediateSize = 2_624;
    private const int AttentionHeads = 16;
    private const int HeadDimension = 64;
    private const int HeadAttentionHeads = 16;
    private const float NormEpsilon = 1e-5f;
    private const int LocalHalfWindow = 64;

    private readonly IAllocator _allocator;
    private readonly IDisposable? _disposableAllocator;
    private readonly DirectContext _context;
    private readonly DirectEmbedding _tokenEmbedding;
    private readonly DirectEmbedding _typeEmbedding;
    private readonly TsTensor _embeddingNorm;
    private readonly TsTensor _finalNorm;
    private readonly EncoderLayer[] _encoderLayers;
    private readonly HeadLayer[] _headLayers;
    private readonly TsTensor _scorerNormWeight;
    private readonly TsTensor _scorerNormBias;
    private readonly DirectLinear _scorerHidden;
    private readonly DirectLinear _scorerOutput;
    private readonly DirectLinear _actionHidden;
    private readonly DirectLinear _actionOutput;
    private bool _disposed;

    private LayaDecisionModel(IFloatTensorStore weights, bool useCuda)
    {
        _allocator = useCuda ? new CudaAllocator() : new CpuAllocator(BlasEnum.DotNet);
        _disposableAllocator = _allocator as IDisposable;
        _context = new DirectContext(_allocator);
        try
        {
            _tokenEmbedding = DirectEmbedding.FromTensorStore(
                _context,
                weights,
                "encoder.embeddings.tok_embeddings.weight");
            _typeEmbedding = DirectEmbedding.FromTensorStore(_context, weights, "type_emb.weight");
            _embeddingNorm = LoadVector(weights, "encoder.embeddings.norm.weight", HiddenSize);
            _finalNorm = LoadVector(weights, "encoder.final_norm.weight", HiddenSize);
            _encoderLayers = Enumerable.Range(0, 28)
                .Select(index => new EncoderLayer(_context, weights, index))
                .ToArray();
            _headLayers = Enumerable.Range(0, 2)
                .Select(index => new HeadLayer(_context, weights, index))
                .ToArray();
            _scorerNormWeight = LoadVector(weights, "scorer.0.weight", HiddenSize);
            _scorerNormBias = LoadVector(weights, "scorer.0.bias", HiddenSize);
            _scorerHidden = DirectLinear.FromTensorStore(
                _context,
                weights,
                "scorer.1.weight",
                "scorer.1.bias");
            _scorerOutput = DirectLinear.FromTensorStore(
                _context,
                weights,
                "scorer.3.weight",
                "scorer.3.bias");
            _actionHidden = DirectLinear.FromTensorStore(
                _context,
                weights,
                "act_head.0.weight",
                "act_head.0.bias");
            _actionOutput = DirectLinear.FromTensorStore(
                _context,
                weights,
                "act_head.2.weight",
                "act_head.2.bias");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static LayaDecisionModel Load(SafetensorsFile weights, bool useCuda)
    {
        ArgumentNullException.ThrowIfNull(weights);
        return new LayaDecisionModel(weights, useCuda);
    }

    public LayaModelOutput Forward(LayaSequence sequence)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sequence.TokenIds.Length == 0)
        {
            throw new ArgumentException("Laya requires at least one token.", nameof(sequence));
        }

        if (sequence.MarkerPositions.Length == 0)
        {
            throw new ArgumentException("Laya requires at least one decision marker.", nameof(sequence));
        }

        TsTensor? hidden = null;
        TsTensor? slidingMask = null;
        try
        {
            hidden = _tokenEmbedding.Forward(sequence.TokenIds);
            hidden = Replace(hidden, DirectOps.LayerNorm(_context, hidden, _embeddingNorm, null!, NormEpsilon));
            int tokenCount = sequence.TokenIds.Length;
            using RopeTables fullRope = RopeTables.Create(_context, tokenCount, 160_000f);
            using RopeTables slidingRope = RopeTables.Create(_context, tokenCount, 10_000f);
            if (tokenCount > LocalHalfWindow + 1)
            {
                slidingMask = CreateSlidingAttentionMask(tokenCount);
            }

            for (int index = 0; index < _encoderLayers.Length; index++)
            {
                bool sliding = index % 3 != 0;
                hidden = Replace(
                    hidden,
                    _encoderLayers[index].Forward(
                        hidden,
                        sliding ? slidingRope : fullRope,
                        sliding ? slidingMask : null));
            }

            hidden = Replace(hidden, DirectOps.LayerNorm(_context, hidden, _finalNorm, null!, NormEpsilon));
            using (TsTensor type = _typeEmbedding.Forward([sequence.QuestionType]))
            using (TsTensor typeVector = type.View(HiddenSize))
            {
                DirectOps.AddBiasRows(_context, hidden, typeVector);
            }

            foreach (HeadLayer layer in _headLayers)
            {
                hidden = Replace(hidden, layer.Forward(hidden));
            }

            float[] logits = ScoreMarkers(hidden, sequence.MarkerPositions);
            float actionProbability = ScoreAction(hidden, logits);
            return new LayaModelOutput(logits, actionProbability);
        }
        finally
        {
            slidingMask?.Dispose();
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
        if (_encoderLayers is not null)
        {
            foreach (EncoderLayer layer in _encoderLayers)
            {
                layer.Dispose();
            }
        }

        if (_headLayers is not null)
        {
            foreach (HeadLayer layer in _headLayers)
            {
                layer.Dispose();
            }
        }

        _scorerHidden?.Dispose();
        _scorerOutput?.Dispose();
        _actionHidden?.Dispose();
        _actionOutput?.Dispose();
        _tokenEmbedding?.Dispose();
        _typeEmbedding?.Dispose();
        _context?.Dispose();
        _disposableAllocator?.Dispose();
    }

    private float[] ScoreMarkers(TsTensor hidden, int[] markerPositions)
    {
        using TsTensor markers = GatherRows(hidden, markerPositions);
        using TsTensor normalized = DirectOps.LayerNorm(
            _context,
            markers,
            _scorerNormWeight,
            _scorerNormBias,
            NormEpsilon);
        using TsTensor projected = _scorerHidden.Forward(normalized);
        using TsTensor activated = DirectOps.Gelu(_context, projected);
        using TsTensor logits = _scorerOutput.Forward(activated);
        return DirectOps.ToArray(logits);
    }

    private float ScoreAction(TsTensor hidden, float[] logits)
    {
        float[] probabilities = Softmax(logits);
        float top1 = probabilities.Max();
        float top2 = probabilities.Length > 1
            ? probabilities.OrderDescending().Skip(1).First()
            : 0f;
        float entropy = 0f;
        foreach (float probability in probabilities)
        {
            entropy -= probability * MathF.Log(MathF.Max(probability, 1e-9f));
        }

        int normalizedCount = Math.Max(2, probabilities.Length);
        entropy /= MathF.Log(normalizedCount);
        using TsTensor pooled = GatherRows(hidden, [0]);
        float[] features = new float[HiddenSize + 4];
        DirectOps.ToArray(pooled).CopyTo(features, 0);
        features[HiddenSize] = top1;
        features[HiddenSize + 1] = top1 - top2;
        features[HiddenSize + 2] = entropy;
        features[HiddenSize + 3] = normalizedCount / 255f;
        using TsTensor input = _context.FromFloats(features, 1, features.Length);
        using TsTensor projected = _actionHidden.Forward(input);
        using TsTensor activated = DirectOps.Gelu(_context, projected);
        using TsTensor output = _actionOutput.Forward(activated);
        return Softmax(DirectOps.ToArray(output))[0];
    }

    private TsTensor GatherRows(TsTensor source, int[] rows)
    {
        using var indices = new TsTensor(_allocator, DType.Int32, rows.LongLength);
        indices.SetElementsAsInt(rows);
        return Ops.IndexSelect(null!, source, indices);
    }

    private TsTensor CreateSlidingAttentionMask(int tokenCount)
    {
        var values = new float[checked(AttentionHeads * tokenCount * tokenCount)];
        int plane = checked(tokenCount * tokenCount);
        for (int row = 0; row < tokenCount; row++)
        {
            for (int column = 0; column < tokenCount; column++)
            {
                float value = Math.Abs(row - column) <= LocalHalfWindow ? 0f : -1e9f;
                for (int head = 0; head < AttentionHeads; head++)
                {
                    values[head * plane + row * tokenCount + column] = value;
                }
            }
        }

        return _context.FromFloats(values, AttentionHeads, tokenCount, tokenCount);
    }

    private TsTensor LoadVector(IFloatTensorStore weights, string name, int length)
    {
        long[] shape = weights.TensorShape(name);
        if (shape.Length != 1 || shape[0] != length)
        {
            throw new InvalidDataException($"Laya weight '{name}' must have shape [{length}].");
        }

        return _context.Own(_context.FromFloats(weights.ReadFloat32(name), length));
    }

    private static TsTensor Replace(TsTensor current, TsTensor replacement)
    {
        current.Dispose();
        return replacement;
    }

    internal static float[] Softmax(ReadOnlySpan<float> logits)
    {
        float maximum = logits.ToArray().Max();
        var probabilities = new float[logits.Length];
        float total = 0f;
        for (int index = 0; index < logits.Length; index++)
        {
            probabilities[index] = MathF.Exp(logits[index] - maximum);
            total += probabilities[index];
        }

        for (int index = 0; index < probabilities.Length; index++)
        {
            probabilities[index] /= total;
        }

        return probabilities;
    }

    private sealed class EncoderLayer : IDisposable
    {
        private readonly DirectContext _context;
        private readonly TsTensor? _attentionNorm;
        private readonly TsTensor _mlpNorm;
        private readonly DirectLinear _qkv;
        private readonly DirectLinear _attentionOutput;
        private readonly DirectLinear _mlpInput;
        private readonly DirectLinear _mlpOutput;

        public EncoderLayer(DirectContext context, IFloatTensorStore weights, int index)
        {
            _context = context;
            string prefix = $"encoder.layers.{index}";
            if (index != 0)
            {
                _attentionNorm = LoadVector(context, weights, $"{prefix}.attn_norm.weight", HiddenSize);
            }

            _mlpNorm = LoadVector(context, weights, $"{prefix}.mlp_norm.weight", HiddenSize);
            _qkv = DirectLinear.FromTensorStore(context, weights, $"{prefix}.attn.Wqkv.weight");
            _attentionOutput = DirectLinear.FromTensorStore(context, weights, $"{prefix}.attn.Wo.weight");
            _mlpInput = DirectLinear.FromTensorStore(context, weights, $"{prefix}.mlp.Wi.weight");
            _mlpOutput = DirectLinear.FromTensorStore(context, weights, $"{prefix}.mlp.Wo.weight");
        }

        public TsTensor Forward(TsTensor input, RopeTables rope, TsTensor? attentionMask)
        {
            TsTensor? attentionInput = null;
            try
            {
                attentionInput = _attentionNorm is null
                    ? input.CopyRef()
                    : DirectOps.LayerNorm(_context, input, _attentionNorm, null!, NormEpsilon);
                using TsTensor qkv = _qkv.Forward(attentionInput);
                using TsTensor queryView = qkv.Narrow(1, 0, HiddenSize);
                using TsTensor keyView = qkv.Narrow(1, HiddenSize, HiddenSize);
                using TsTensor valueView = qkv.Narrow(1, HiddenSize * 2, HiddenSize);
                using TsTensor query = Ops.NewContiguous(queryView);
                using TsTensor key = Ops.NewContiguous(keyView);
                using TsTensor value = Ops.NewContiguous(valueView);
                ApplyNeoxRope(_context, query, rope);
                ApplyNeoxRope(_context, key, rope);
                using TsTensor attended = DirectOps.Attention(
                    _context,
                    query,
                    key,
                    value,
                    AttentionHeads,
                    HeadDimension,
                    1f / MathF.Sqrt(HeadDimension),
                    attentionMask!);
                using TsTensor attentionOutput = _attentionOutput.Forward(attended);
                TsTensor residual = _context.NewF32(input.Sizes);
                try
                {
                    Ops.Add(residual, input, attentionOutput);
                    using TsTensor normalized = DirectOps.LayerNorm(
                        _context,
                        residual,
                        _mlpNorm,
                        null!,
                        NormEpsilon);
                    using TsTensor expanded = _mlpInput.Forward(normalized);
                    using TsTensor inputView = expanded.Narrow(1, 0, IntermediateSize);
                    using TsTensor gateView = expanded.Narrow(1, IntermediateSize, IntermediateSize);
                    using TsTensor mlpInput = Ops.NewContiguous(inputView);
                    using TsTensor gate = Ops.NewContiguous(gateView);
                    using TsTensor activated = DirectOps.Gelu(_context, mlpInput);
                    using TsTensor gated = _context.NewF32(activated.Sizes);
                    Ops.Mul(gated, activated, gate);
                    using TsTensor projected = _mlpOutput.Forward(gated);
                    TsTensor output = _context.NewF32(residual.Sizes);
                    try
                    {
                        Ops.Add(output, residual, projected);
                        return output;
                    }
                    catch
                    {
                        output.Dispose();
                        throw;
                    }
                }
                finally
                {
                    residual.Dispose();
                }
            }
            finally
            {
                attentionInput?.Dispose();
            }
        }

        public void Dispose()
        {
            _qkv.Dispose();
            _attentionOutput.Dispose();
            _mlpInput.Dispose();
            _mlpOutput.Dispose();
        }
    }

    private sealed class HeadLayer : IDisposable
    {
        private readonly DirectContext _context;
        private readonly TsTensor _norm1Weight;
        private readonly TsTensor _norm1Bias;
        private readonly TsTensor _norm2Weight;
        private readonly TsTensor _norm2Bias;
        private readonly DirectLinear _qkv;
        private readonly DirectLinear _attentionOutput;
        private readonly DirectLinear _linear1;
        private readonly DirectLinear _linear2;

        public HeadLayer(DirectContext context, IFloatTensorStore weights, int index)
        {
            _context = context;
            string prefix = $"head.layers.{index}";
            _norm1Weight = LoadVector(context, weights, $"{prefix}.norm1.weight", HiddenSize);
            _norm1Bias = LoadVector(context, weights, $"{prefix}.norm1.bias", HiddenSize);
            _norm2Weight = LoadVector(context, weights, $"{prefix}.norm2.weight", HiddenSize);
            _norm2Bias = LoadVector(context, weights, $"{prefix}.norm2.bias", HiddenSize);
            _qkv = DirectLinear.FromTensorStore(
                context,
                weights,
                $"{prefix}.self_attn.in_proj_weight",
                $"{prefix}.self_attn.in_proj_bias");
            _attentionOutput = DirectLinear.FromTensorStore(
                context,
                weights,
                $"{prefix}.self_attn.out_proj.weight",
                $"{prefix}.self_attn.out_proj.bias");
            _linear1 = DirectLinear.FromTensorStore(
                context,
                weights,
                $"{prefix}.linear1.weight",
                $"{prefix}.linear1.bias");
            _linear2 = DirectLinear.FromTensorStore(
                context,
                weights,
                $"{prefix}.linear2.weight",
                $"{prefix}.linear2.bias");
        }

        public TsTensor Forward(TsTensor input)
        {
            using TsTensor normalized1 = DirectOps.LayerNorm(
                _context,
                input,
                _norm1Weight,
                _norm1Bias,
                NormEpsilon);
            using TsTensor qkv = _qkv.Forward(normalized1);
            using TsTensor queryView = qkv.Narrow(1, 0, HiddenSize);
            using TsTensor keyView = qkv.Narrow(1, HiddenSize, HiddenSize);
            using TsTensor valueView = qkv.Narrow(1, HiddenSize * 2, HiddenSize);
            using TsTensor query = Ops.NewContiguous(queryView);
            using TsTensor key = Ops.NewContiguous(keyView);
            using TsTensor value = Ops.NewContiguous(valueView);
            using TsTensor attended = DirectOps.Attention(
                _context,
                query,
                key,
                value,
                HeadAttentionHeads,
                HeadDimension,
                1f / MathF.Sqrt(HeadDimension));
            using TsTensor attentionOutput = _attentionOutput.Forward(attended);
            using TsTensor attentionResidual = _context.NewF32(input.Sizes);
            Ops.Add(attentionResidual, input, attentionOutput);
            using TsTensor normalized2 = DirectOps.LayerNorm(
                _context,
                attentionResidual,
                _norm2Weight,
                _norm2Bias,
                NormEpsilon);
            using TsTensor expanded = _linear1.Forward(normalized2);
            using TsTensor activated = _context.NewF32(expanded.Sizes);
            Ops.Relu(activated, expanded);
            using TsTensor projected = _linear2.Forward(activated);
            TsTensor output = _context.NewF32(input.Sizes);
            try
            {
                Ops.Add(output, attentionResidual, projected);
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _qkv.Dispose();
            _attentionOutput.Dispose();
            _linear1.Dispose();
            _linear2.Dispose();
        }
    }

    private sealed class RopeTables : IDisposable
    {
        private RopeTables(TsTensor cosine, TsTensor sine)
        {
            Cosine = cosine;
            Sine = sine;
        }

        public TsTensor Cosine { get; }

        public TsTensor Sine { get; }

        public static RopeTables Create(DirectContext context, int tokenCount, float theta)
        {
            int half = HeadDimension / 2;
            var cosine = new float[tokenCount * half];
            var sine = new float[tokenCount * half];
            for (int position = 0; position < tokenCount; position++)
            {
                for (int dimension = 0; dimension < half; dimension++)
                {
                    float inverseFrequency = 1f / MathF.Pow(theta, 2f * dimension / HeadDimension);
                    float angle = position * inverseFrequency;
                    cosine[position * half + dimension] = MathF.Cos(angle);
                    sine[position * half + dimension] = MathF.Sin(angle);
                }
            }

            return new RopeTables(
                context.FromFloats(cosine, tokenCount, half),
                context.FromFloats(sine, tokenCount, half));
        }

        public void Dispose()
        {
            Cosine.Dispose();
            Sine.Dispose();
        }
    }

    private static TsTensor LoadVector(
        DirectContext context,
        IFloatTensorStore weights,
        string name,
        int length)
    {
        long[] shape = weights.TensorShape(name);
        if (shape.Length != 1 || shape[0] != length)
        {
            throw new InvalidDataException($"Laya weight '{name}' must have shape [{length}].");
        }

        return context.Own(context.FromFloats(weights.ReadFloat32(name), length));
    }

    private static unsafe void ApplyNeoxRope(DirectContext context, TsTensor data, RopeTables rope)
    {
        int tokenCount = checked((int)data.Sizes[0]);
        int half = HeadDimension / 2;
        if (context.IsCuda)
        {
            if (!CudaFusedOps.TryNeoxRopeFlat(
                    data,
                    rope.Cosine,
                    rope.Sine,
                    AttentionHeads,
                    tokenCount,
                    HeadDimension,
                    half))
            {
                throw new InvalidOperationException("CUDA NeoX RoPE kernel rejected the Laya tensors.");
            }

            return;
        }

        float* values = (float*)CpuNativeHelpers.GetBufferStart(data);
        float* cosine = (float*)CpuNativeHelpers.GetBufferStart(rope.Cosine);
        float* sine = (float*)CpuNativeHelpers.GetBufferStart(rope.Sine);
        Parallel.For(0, tokenCount * AttentionHeads, tokenHead =>
        {
            int token = tokenHead / AttentionHeads;
            float* row = values + tokenHead * HeadDimension;
            float* cos = cosine + token * half;
            float* sin = sine + token * half;
            for (int dimension = 0; dimension < half; dimension++)
            {
                float first = row[dimension];
                float second = row[dimension + half];
                row[dimension] = first * cos[dimension] - second * sin[dimension];
                row[dimension + half] = second * cos[dimension] + first * sin[dimension];
            }
        });
    }
}
