using Darkspyre.Inference.Tensor.Image.Supra.Direct;
using TensorSharp;
using TensorSharp.Cpu;
using TensorSharp.Cuda;
using TensorSharp.Runtime;

namespace Darkspyre.Inference.Tensor.Image.Supra;

internal sealed class SupraImageModel : ISupraImageModel
{
    private const float VaeScale = 0.18215f;

    private readonly IAllocator _allocator;
    private readonly IDisposable? _disposableAllocator;
    private readonly DirectContext _context;
    private readonly SupraTextTokenizer _tokenizer;
    private readonly FlanT5Encoder _textEncoder = null!;
    private readonly SupraDiT _dit = null!;
    private readonly StableDiffusionVaeDecoder _vae = null!;
    private readonly SupraTextCondition _unconditional;
    private bool _disposed;

    public SupraImageModel(
        SupraTextTokenizer tokenizer,
        IFloatTensorStore textWeights,
        TorchStateDictionaryFile ditWeights,
        IFloatTensorStore vaeWeights,
        bool useCuda)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(textWeights);
        ArgumentNullException.ThrowIfNull(ditWeights);
        ArgumentNullException.ThrowIfNull(vaeWeights);
        _tokenizer = tokenizer;
        _allocator = useCuda ? new CudaAllocator() : new CpuAllocator(BlasEnum.DotNet);
        _disposableAllocator = _allocator as IDisposable;
        _context = new DirectContext(_allocator);
        try
        {
            _textEncoder = new FlanT5Encoder(_context, textWeights);
            _dit = new SupraDiT(_context, ditWeights);
            _vae = new StableDiffusionVaeDecoder(_context, vaeWeights);
            float[] hidden = ditWeights.ReadFloat32("config.uncond_text");
            float[] mask = ditWeights.ReadFloat32("config.uncond_mask");
            int tokenCount = PrefixLength(mask);
            _unconditional = new SupraTextCondition(hidden, mask, tokenCount);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public byte[] Generate(
        string prompt,
        long seed,
        int steps,
        float guidanceScale,
        Action<int, int>? stepCompleted,
        Action? decodingStarted,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SupraTextCondition condition = _textEncoder.Encode(_tokenizer, prompt);
        float[] latent = CreateNoise(seed);
        float stepSize = 1f / steps;
        for (int step = 0; step < steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float timestep = step * stepSize;
            float[] conditional = _dit.Predict(latent, timestep, condition);
            if (guidanceScale > 1f)
            {
                float[] unconditional = _dit.Predict(latent, timestep, _unconditional);
                for (int index = 0; index < latent.Length; index++)
                {
                    float velocity = unconditional[index]
                        + guidanceScale * (conditional[index] - unconditional[index]);
                    latent[index] += stepSize * velocity;
                }
            }
            else
            {
                for (int index = 0; index < latent.Length; index++)
                {
                    latent[index] += stepSize * conditional[index];
                }
            }

            stepCompleted?.Invoke(step + 1, steps);
        }

        cancellationToken.ThrowIfCancellationRequested();
        for (int index = 0; index < latent.Length; index++)
        {
            latent[index] /= VaeScale;
        }

        decodingStarted?.Invoke();
        return _vae.Decode(latent);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _vae?.Dispose();
        _dit?.Dispose();
        _textEncoder?.Dispose();
        _context.Dispose();
        _disposableAllocator?.Dispose();
    }

    private static int PrefixLength(float[] mask)
    {
        int count = 0;
        while (count < mask.Length && mask[count] > 0f)
        {
            count++;
        }

        if (count == 0 || mask.AsSpan(count).ContainsAnyExcept(0f))
        {
            throw new InvalidDataException("Supra unconditional attention mask is not a non-empty prefix mask.");
        }

        return count;
    }

    private static float[] CreateNoise(long seed)
    {
        var random = new GaussianRandom(unchecked((ulong)seed));
        var result = new float[SupraDiT.LatentElementCount];
        for (int index = 0; index < result.Length; index += 2)
        {
            (float first, float second) = random.NextPair();
            result[index] = first;
            if (index + 1 < result.Length)
            {
                result[index + 1] = second;
            }
        }

        return result;
    }

    private sealed class GaussianRandom
    {
        private ulong _state;

        internal GaussianRandom(ulong seed)
        {
            _state = seed;
        }

        internal (float First, float Second) NextPair()
        {
            double first = Math.Max(NextUnit(), double.Epsilon);
            double second = NextUnit();
            double magnitude = Math.Sqrt(-2d * Math.Log(first));
            double angle = 2d * Math.PI * second;
            return ((float)(magnitude * Math.Cos(angle)), (float)(magnitude * Math.Sin(angle)));
        }

        private double NextUnit()
        {
            ulong value = NextUInt64() >> 11;
            return value * (1d / (1UL << 53));
        }

        private ulong NextUInt64()
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
