using Darkspyre.Inference.Tensor.Image.Supra.Direct;
using TensorSharp;
using TensorSharp.Runtime;
using TsTensor = TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.Image.Supra;

internal sealed class StableDiffusionVaeDecoder : IDisposable
{
    private const int GroupCount = 32;
    private const float NormEpsilon = 1e-6f;

    private readonly DirectContext _context;
    private readonly DirectConv2D _postQuant;
    private readonly DirectConv2D _input;
    private readonly ResnetBlock _midFirst;
    private readonly AttentionBlock _midAttention;
    private readonly ResnetBlock _midSecond;
    private readonly UpBlock[] _upBlocks;
    private readonly Norm _outputNorm;
    private readonly DirectConv2D _output;
    private bool _disposed;

    public StableDiffusionVaeDecoder(DirectContext context, IFloatTensorStore weights)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(weights);
        _context = context;
        try
        {
            _postQuant = Conv(weights, "post_quant_conv");
            _input = Conv(weights, "decoder.conv_in");
            _midFirst = new ResnetBlock(context, weights, "decoder.mid_block.resnets.0");
            _midAttention = new AttentionBlock(context, weights, "decoder.mid_block.attentions.0");
            _midSecond = new ResnetBlock(context, weights, "decoder.mid_block.resnets.1");
            _upBlocks = Enumerable.Range(0, 4)
                .Select(index => new UpBlock(context, weights, index))
                .ToArray();
            _outputNorm = Norm.Load(context, weights, "decoder.conv_norm_out");
            _output = Conv(weights, "decoder.conv_out");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public byte[] Decode(float[] latent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(latent);
        if (latent.Length != SupraDiT.LatentElementCount)
        {
            throw new ArgumentException(
                $"Stable Diffusion VAE latent must contain {SupraDiT.LatentElementCount} values.",
                nameof(latent));
        }

        TsTensor? hidden = null;
        try
        {
            using TsTensor input = _context.FromFloats(
                ChannelsFirstToLast(latent, SupraDiT.LatentChannels, SupraDiT.LatentSize),
                1,
                SupraDiT.LatentSize,
                SupraDiT.LatentSize,
                SupraDiT.LatentChannels);
            hidden = _postQuant.Forward(input);
            hidden = Replace(hidden, _input.Forward(hidden, paddingHeight: 1, paddingWidth: 1));
            hidden = Replace(hidden, _midFirst.Forward(hidden));
            _midAttention.Forward(hidden);
            hidden = Replace(hidden, _midSecond.Forward(hidden));
            foreach (UpBlock block in _upBlocks)
            {
                hidden = Replace(hidden, block.Forward(hidden));
            }

            using TsTensor normalized = DirectImageOps.GroupNorm(
                _context,
                hidden,
                GroupCount,
                _outputNorm.Gain,
                _outputNorm.Bias,
                NormEpsilon);
            using TsTensor activated = DirectOps.Silu(_context, normalized);
            using TsTensor pixels = _output.Forward(activated, paddingHeight: 1, paddingWidth: 1);
            return ToRgb8(DirectOps.ToArray(pixels));
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
        _postQuant?.Dispose();
        _input?.Dispose();
        _midFirst?.Dispose();
        _midAttention?.Dispose();
        _midSecond?.Dispose();
        if (_upBlocks is not null)
        {
            foreach (UpBlock block in _upBlocks)
            {
                block.Dispose();
            }
        }

        _output?.Dispose();
    }

    private DirectConv2D Conv(IFloatTensorStore weights, string prefix) =>
        DirectConv2D.FromTensorStore(_context, weights, $"{prefix}.weight", $"{prefix}.bias");

    private static float[] ChannelsFirstToLast(float[] source, int channels, int size)
    {
        var result = new float[source.Length];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    result[(y * size + x) * channels + channel] =
                        source[(channel * size + y) * size + x];
                }
            }
        }

        return result;
    }

    private static byte[] ToRgb8(float[] pixels)
    {
        int expected = SupraDiT.ImageSize * SupraDiT.ImageSize * 3;
        if (pixels.Length != expected)
        {
            throw new InvalidDataException($"Stable Diffusion VAE returned {pixels.Length} values; expected {expected}.");
        }

        var result = new byte[expected];
        for (int index = 0; index < pixels.Length; index++)
        {
            float normalized = (Math.Clamp(pixels[index], -1f, 1f) + 1f) * 0.5f;
            result[index] = (byte)Math.Clamp((int)MathF.Round(normalized * 255f), 0, 255);
        }

        return result;
    }

    private static TsTensor Replace(TsTensor previous, TsTensor replacement)
    {
        previous.Dispose();
        return replacement;
    }

    private sealed class Norm
    {
        internal required TsTensor Gain { get; init; }
        internal required TsTensor Bias { get; init; }

        internal static Norm Load(DirectContext context, IFloatTensorStore weights, string prefix)
        {
            long[] gainShape = weights.TensorShape($"{prefix}.weight");
            long[] biasShape = weights.TensorShape($"{prefix}.bias");
            if (gainShape.Length != 1 || !gainShape.SequenceEqual(biasShape))
            {
                throw new InvalidDataException($"VAE norm '{prefix}' has incompatible affine tensors.");
            }

            return new Norm
            {
                Gain = context.Own(context.FromFloats(weights.ReadFloat32($"{prefix}.weight"), gainShape)),
                Bias = context.Own(context.FromFloats(weights.ReadFloat32($"{prefix}.bias"), biasShape))
            };
        }
    }

    private sealed class ResnetBlock : IDisposable
    {
        private readonly DirectContext _context;
        private readonly Norm _firstNorm;
        private readonly Norm _secondNorm;
        private readonly DirectConv2D _firstConv;
        private readonly DirectConv2D _secondConv;
        private readonly DirectConv2D? _shortcut;

        internal ResnetBlock(DirectContext context, IFloatTensorStore weights, string prefix)
        {
            _context = context;
            _firstNorm = Norm.Load(context, weights, $"{prefix}.norm1");
            _secondNorm = Norm.Load(context, weights, $"{prefix}.norm2");
            _firstConv = DirectConv2D.FromTensorStore(
                context,
                weights,
                $"{prefix}.conv1.weight",
                $"{prefix}.conv1.bias");
            _secondConv = DirectConv2D.FromTensorStore(
                context,
                weights,
                $"{prefix}.conv2.weight",
                $"{prefix}.conv2.bias");
            if (weights.HasTensor($"{prefix}.conv_shortcut.weight"))
            {
                _shortcut = DirectConv2D.FromTensorStore(
                    context,
                    weights,
                    $"{prefix}.conv_shortcut.weight",
                    $"{prefix}.conv_shortcut.bias");
            }
        }

        internal TsTensor Forward(TsTensor input)
        {
            using TsTensor firstNorm = DirectImageOps.GroupNorm(
                _context,
                input,
                GroupCount,
                _firstNorm.Gain,
                _firstNorm.Bias,
                NormEpsilon);
            using TsTensor firstActivation = DirectOps.Silu(_context, firstNorm);
            using TsTensor firstOutput = _firstConv.Forward(firstActivation, paddingHeight: 1, paddingWidth: 1);
            using TsTensor secondNorm = DirectImageOps.GroupNorm(
                _context,
                firstOutput,
                GroupCount,
                _secondNorm.Gain,
                _secondNorm.Bias,
                NormEpsilon);
            using TsTensor secondActivation = DirectOps.Silu(_context, secondNorm);
            TsTensor output = _secondConv.Forward(secondActivation, paddingHeight: 1, paddingWidth: 1);
            TsTensor? projectedShortcut = null;
            try
            {
                TsTensor shortcut = input;
                if (_shortcut is not null)
                {
                    projectedShortcut = _shortcut.Forward(input);
                    shortcut = projectedShortcut;
                }

                Ops.Add(output, output, shortcut);
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
            finally
            {
                projectedShortcut?.Dispose();
            }
        }

        public void Dispose()
        {
            _firstConv.Dispose();
            _secondConv.Dispose();
            _shortcut?.Dispose();
        }
    }

    private sealed class AttentionBlock : IDisposable
    {
        private readonly DirectContext _context;
        private readonly Norm _norm;
        private readonly DirectLinear _query;
        private readonly DirectLinear _key;
        private readonly DirectLinear _value;
        private readonly DirectLinear _output;
        private readonly int _channels;

        internal AttentionBlock(DirectContext context, IFloatTensorStore weights, string prefix)
        {
            _context = context;
            _norm = Norm.Load(context, weights, $"{prefix}.group_norm");
            _query = Linear(weights, $"{prefix}.query");
            _key = Linear(weights, $"{prefix}.key");
            _value = Linear(weights, $"{prefix}.value");
            _output = Linear(weights, $"{prefix}.proj_attn");
            _channels = checked((int)_norm.Gain.Sizes[0]);
        }

        internal void Forward(TsTensor input)
        {
            long height = input.Sizes[1];
            long width = input.Sizes[2];
            using TsTensor normalized = DirectImageOps.GroupNorm(
                _context,
                input,
                GroupCount,
                _norm.Gain,
                _norm.Bias,
                NormEpsilon);
            using TsTensor rows = normalized.View(height * width, _channels);
            using TsTensor query = _query.Forward(rows);
            using TsTensor key = _key.Forward(rows);
            using TsTensor value = _value.Forward(rows);
            using TsTensor attention = DirectOps.Attention(
                _context,
                query,
                key,
                value,
                heads: 1,
                headDim: _channels,
                scale: 1f / MathF.Sqrt(_channels));
            using TsTensor projected = _output.Forward(attention);
            using TsTensor image = projected.View(1, height, width, _channels);
            Ops.Add(input, input, image);
        }

        public void Dispose()
        {
            _query.Dispose();
            _key.Dispose();
            _value.Dispose();
            _output.Dispose();
        }

        private DirectLinear Linear(IFloatTensorStore weights, string prefix) =>
            DirectLinear.FromTensorStore(_context, weights, $"{prefix}.weight", $"{prefix}.bias");
    }

    private sealed class UpBlock : IDisposable
    {
        private readonly DirectContext _context;
        private readonly ResnetBlock[] _resnets;
        private readonly DirectConv2D? _upsampler;

        internal UpBlock(DirectContext context, IFloatTensorStore weights, int index)
        {
            _context = context;
            string prefix = $"decoder.up_blocks.{index}";
            _resnets = Enumerable.Range(0, 3)
                .Select(resnet => new ResnetBlock(context, weights, $"{prefix}.resnets.{resnet}"))
                .ToArray();
            if (weights.HasTensor($"{prefix}.upsamplers.0.conv.weight"))
            {
                _upsampler = DirectConv2D.FromTensorStore(
                    context,
                    weights,
                    $"{prefix}.upsamplers.0.conv.weight",
                    $"{prefix}.upsamplers.0.conv.bias");
            }
        }

        internal TsTensor Forward(TsTensor input)
        {
            TsTensor hidden = input.CopyRef();
            try
            {
                foreach (ResnetBlock resnet in _resnets)
                {
                    hidden = Replace(hidden, resnet.Forward(hidden));
                }

                if (_upsampler is not null)
                {
                    hidden = Replace(hidden, DirectImageOps.NearestUpsample2D(_context, hidden));
                    hidden = Replace(hidden, _upsampler.Forward(hidden, paddingHeight: 1, paddingWidth: 1));
                }

                return hidden;
            }
            catch
            {
                hidden.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (ResnetBlock resnet in _resnets)
            {
                resnet.Dispose();
            }

            _upsampler?.Dispose();
        }
    }
}
