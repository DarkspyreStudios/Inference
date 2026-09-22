using TensorSharp;
using TensorSharp.Models.Direct;
using TensorSharp.Runtime;
using TsTensor = TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.Image;

internal sealed class SupraDiT : IDisposable
{
    public const int ImageSize = 256;
    public const int LatentSize = 32;
    public const int LatentChannels = 4;
    public const int LatentElementCount = LatentChannels * LatentSize * LatentSize;

    private const int PatchSize = 2;
    private const int TokenCount = 256;
    private const int PatchValues = 16;
    private const int HiddenSize = 576;
    private const int TimestepSize = 256;
    private const int AttentionHeads = 9;
    private const int HeadDimension = 64;
    private const int LayerCount = 14;
    private const float NormEpsilon = 1e-6f;

    private readonly DirectContext _context;
    private readonly DirectLinear _inputProjection;
    private readonly DirectLinear _timestepInput;
    private readonly DirectLinear _timestepOutput;
    private readonly DirectLinear _contextProjection;
    private readonly TsTensor _positionEmbedding;
    private readonly DiTBlock[] _blocks;
    private readonly DirectLinear _finalAdaLn;
    private readonly DirectLinear _finalProjection;
    private bool _disposed;

    public SupraDiT(DirectContext context, IFloatTensorStore weights)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(weights);
        _context = context;
        try
        {
            _inputProjection = Linear(weights, "x_embed");
            _timestepInput = Linear(weights, "t_embed.mlp.0");
            _timestepOutput = Linear(weights, "t_embed.mlp.2");
            _contextProjection = Linear(weights, "ctx_proj");
            _positionEmbedding = LoadPositionEmbedding(weights);
            _blocks = Enumerable.Range(0, LayerCount)
                .Select(index => new DiTBlock(context, weights, index))
                .ToArray();
            _finalAdaLn = Linear(weights, "final.adaln.1");
            _finalProjection = Linear(weights, "final.linear");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public float[] Predict(float[] latent, float timestep, SupraTextCondition condition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(latent);
        if (latent.Length != LatentElementCount)
        {
            throw new ArgumentException($"Supra latent must contain {LatentElementCount} values.", nameof(latent));
        }

        if (condition.TokenCount is <= 0 or > SupraTextTokenizer.MaximumLength)
        {
            throw new ArgumentException("Supra text condition has an invalid token count.", nameof(condition));
        }

        float[] contextValues = condition.HiddenStates.AsSpan(
            0,
            condition.TokenCount * FlanT5Encoder.HiddenSize).ToArray();
        TsTensor? hidden = null;
        try
        {
            using TsTensor patches = _context.FromFloats(Patchify(latent), TokenCount, PatchValues);
            hidden = _inputProjection.Forward(patches);
            Ops.Add(hidden, hidden, _positionEmbedding);

            using TsTensor timestepInput = _context.FromFloats(
                TimestepEmbedding(timestep),
                1,
                TimestepSize);
            using TsTensor timestepHidden = _timestepInput.Forward(timestepInput);
            using TsTensor timestepActivated = DirectOps.Silu(_context, timestepHidden);
            using TsTensor timeCondition = _timestepOutput.Forward(timestepActivated);

            using TsTensor sourceContext = _context.FromFloats(
                contextValues,
                condition.TokenCount,
                FlanT5Encoder.HiddenSize);
            using TsTensor projectedContext = _contextProjection.Forward(sourceContext);
            foreach (DiTBlock block in _blocks)
            {
                block.Forward(hidden, timeCondition, projectedContext);
            }

            using TsTensor finalParameters = ProjectActivated(_finalAdaLn, timeCondition);
            using TsTensor shift = finalParameters.Narrow(1, 0, HiddenSize);
            using TsTensor scale = finalParameters.Narrow(1, HiddenSize, HiddenSize);
            using TsTensor normalized = DirectOps.LayerNorm(_context, hidden, null!, null!, NormEpsilon);
            DirectOps.ModulateRows(_context, normalized, normalized, shift, scale, 0, TokenCount);
            using TsTensor outputTokens = _finalProjection.Forward(normalized);
            return Unpatchify(DirectOps.ToArray(outputTokens));
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
        _inputProjection?.Dispose();
        _timestepInput?.Dispose();
        _timestepOutput?.Dispose();
        _contextProjection?.Dispose();
        _finalAdaLn?.Dispose();
        _finalProjection?.Dispose();
        if (_blocks is not null)
        {
            foreach (DiTBlock block in _blocks)
            {
                block.Dispose();
            }
        }
    }

    private DirectLinear Linear(IFloatTensorStore weights, string prefix) =>
        DirectLinear.FromTensorStore(_context, weights, $"{prefix}.weight", $"{prefix}.bias");

    private TsTensor LoadPositionEmbedding(IFloatTensorStore weights)
    {
        long[] shape = weights.TensorShape("pos_embed");
        if (!shape.SequenceEqual([1, TokenCount, HiddenSize]))
        {
            throw new InvalidDataException(
                $"Supra position embedding must have shape [1, {TokenCount}, {HiddenSize}].");
        }

        return _context.Own(_context.FromFloats(
            weights.ReadFloat32("pos_embed"),
            TokenCount,
            HiddenSize));
    }

    private TsTensor ProjectActivated(DirectLinear projection, TsTensor input)
    {
        using TsTensor activated = DirectOps.Silu(_context, input);
        return projection.Forward(activated);
    }

    private static float[] TimestepEmbedding(float timestep)
    {
        var result = new float[TimestepSize];
        int half = TimestepSize / 2;
        for (int index = 0; index < half; index++)
        {
            float frequency = MathF.Exp(-MathF.Log(10_000f) * index / half);
            float angle = timestep * frequency * 1_000f;
            result[index] = MathF.Cos(angle);
            result[half + index] = MathF.Sin(angle);
        }

        return result;
    }

    internal static float[] Patchify(float[] latent)
    {
        var patches = new float[TokenCount * PatchValues];
        int target = 0;
        for (int patchY = 0; patchY < LatentSize / PatchSize; patchY++)
        {
            for (int patchX = 0; patchX < LatentSize / PatchSize; patchX++)
            {
                for (int channel = 0; channel < LatentChannels; channel++)
                {
                    for (int y = 0; y < PatchSize; y++)
                    {
                        for (int x = 0; x < PatchSize; x++)
                        {
                            int sourceY = patchY * PatchSize + y;
                            int sourceX = patchX * PatchSize + x;
                            patches[target++] = latent[
                                (channel * LatentSize + sourceY) * LatentSize + sourceX];
                        }
                    }
                }
            }
        }

        return patches;
    }

    internal static float[] Unpatchify(float[] patches)
    {
        if (patches.Length != TokenCount * PatchValues)
        {
            throw new InvalidDataException("Supra DiT returned an unexpected patch tensor size.");
        }

        var latent = new float[LatentElementCount];
        int source = 0;
        for (int patchY = 0; patchY < LatentSize / PatchSize; patchY++)
        {
            for (int patchX = 0; patchX < LatentSize / PatchSize; patchX++)
            {
                for (int channel = 0; channel < LatentChannels; channel++)
                {
                    for (int y = 0; y < PatchSize; y++)
                    {
                        for (int x = 0; x < PatchSize; x++)
                        {
                            int targetY = patchY * PatchSize + y;
                            int targetX = patchX * PatchSize + x;
                            latent[(channel * LatentSize + targetY) * LatentSize + targetX] = patches[source++];
                        }
                    }
                }
            }
        }

        return latent;
    }

    private sealed class DiTBlock : IDisposable
    {
        private readonly DirectContext _context;
        private readonly DirectLinear _selfQkv;
        private readonly DirectLinear _selfOutput;
        private readonly DirectLinear _crossQuery;
        private readonly DirectLinear _crossKv;
        private readonly DirectLinear _crossOutput;
        private readonly DirectLinear _mlpInput;
        private readonly DirectLinear _mlpOutput;
        private readonly DirectLinear _adaLn;

        internal DiTBlock(DirectContext context, IFloatTensorStore weights, int index)
        {
            _context = context;
            string prefix = $"blocks.{index}";
            _selfQkv = Linear(weights, $"{prefix}.self_attn.qkv");
            _selfOutput = Linear(weights, $"{prefix}.self_attn.proj");
            _crossQuery = Linear(weights, $"{prefix}.cross_attn.q");
            _crossKv = Linear(weights, $"{prefix}.cross_attn.kv");
            _crossOutput = Linear(weights, $"{prefix}.cross_attn.proj");
            _mlpInput = Linear(weights, $"{prefix}.mlp.0");
            _mlpOutput = Linear(weights, $"{prefix}.mlp.2");
            _adaLn = Linear(weights, $"{prefix}.adaln.1");
        }

        internal void Forward(TsTensor hidden, TsTensor timeCondition, TsTensor textContext)
        {
            using TsTensor activatedTime = DirectOps.Silu(_context, timeCondition);
            using TsTensor parameters = _adaLn.Forward(activatedTime);
            using TsTensor shiftSelf = parameters.Narrow(1, 0, HiddenSize);
            using TsTensor scaleSelf = parameters.Narrow(1, HiddenSize, HiddenSize);
            using TsTensor gateSelf = parameters.Narrow(1, 2L * HiddenSize, HiddenSize);
            using TsTensor shiftMlp = parameters.Narrow(1, 3L * HiddenSize, HiddenSize);
            using TsTensor scaleMlp = parameters.Narrow(1, 4L * HiddenSize, HiddenSize);
            using TsTensor gateMlp = parameters.Narrow(1, 5L * HiddenSize, HiddenSize);

            using (TsTensor normalized = DirectOps.LayerNorm(_context, hidden, null!, null!, NormEpsilon))
            {
                DirectOps.ModulateRows(_context, normalized, normalized, shiftSelf, scaleSelf, 0, TokenCount);
                using TsTensor qkv = _selfQkv.Forward(normalized);
                using TsTensor queryView = qkv.Narrow(1, 0, HiddenSize);
                using TsTensor keyView = qkv.Narrow(1, HiddenSize, HiddenSize);
                using TsTensor valueView = qkv.Narrow(1, 2L * HiddenSize, HiddenSize);
                using TsTensor query = Ops.NewContiguous(queryView);
                using TsTensor key = Ops.NewContiguous(keyView);
                using TsTensor value = Ops.NewContiguous(valueView);
                using TsTensor attention = DirectOps.Attention(
                    _context,
                    query,
                    key,
                    value,
                    AttentionHeads,
                    HeadDimension,
                    1f / MathF.Sqrt(HeadDimension));
                using TsTensor projected = _selfOutput.Forward(attention);
                DirectOps.GateAddRows(_context, hidden, projected, gateSelf, 0, TokenCount);
            }

            using (TsTensor normalized = DirectOps.LayerNorm(_context, hidden, null!, null!, NormEpsilon))
            using (TsTensor query = _crossQuery.Forward(normalized))
            using (TsTensor kv = _crossKv.Forward(textContext))
            using (TsTensor keyView = kv.Narrow(1, 0, HiddenSize))
            using (TsTensor valueView = kv.Narrow(1, HiddenSize, HiddenSize))
            using (TsTensor key = Ops.NewContiguous(keyView))
            using (TsTensor value = Ops.NewContiguous(valueView))
            using (TsTensor attention = DirectOps.Attention(
                _context,
                query,
                key,
                value,
                AttentionHeads,
                HeadDimension,
                1f / MathF.Sqrt(HeadDimension)))
            using (TsTensor projected = _crossOutput.Forward(attention))
            {
                Ops.Add(hidden, hidden, projected);
            }

            using (TsTensor normalized = DirectOps.LayerNorm(_context, hidden, null!, null!, NormEpsilon))
            {
                DirectOps.ModulateRows(_context, normalized, normalized, shiftMlp, scaleMlp, 0, TokenCount);
                using TsTensor projected = _mlpInput.Forward(normalized);
                using TsTensor activated = DirectOps.Gelu(_context, projected);
                using TsTensor output = _mlpOutput.Forward(activated);
                DirectOps.GateAddRows(_context, hidden, output, gateMlp, 0, TokenCount);
            }
        }

        public void Dispose()
        {
            _selfQkv.Dispose();
            _selfOutput.Dispose();
            _crossQuery.Dispose();
            _crossKv.Dispose();
            _crossOutput.Dispose();
            _mlpInput.Dispose();
            _mlpOutput.Dispose();
            _adaLn.Dispose();
        }

        private DirectLinear Linear(IFloatTensorStore weights, string prefix) =>
            DirectLinear.FromTensorStore(_context, weights, $"{prefix}.weight", $"{prefix}.bias");
    }
}
