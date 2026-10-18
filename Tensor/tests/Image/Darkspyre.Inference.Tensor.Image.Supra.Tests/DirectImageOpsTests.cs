using Darkspyre.Inference.Tensor.Image.Supra.Direct;
using TensorSharp;
using TensorSharp.Cpu;
using TensorSharp.Runtime;
using TsTensor = TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.Image.Supra.Tests;

/// <summary>Supra's image operations on the managed CPU allocator, against direct managed reference loops over the
/// same channels-last <c>[batch, height, width, channels]</c> layout.</summary>
public sealed class DirectImageOpsTests : IDisposable
{
    private const float Tolerance = 1e-4f;
    private readonly DirectContext _context = new(new CpuAllocator(BlasEnum.DotNet));

    [Fact]
    public void Conv2DMatchesAManagedReference()
    {
        const int height = 5;
        const int width = 4;
        const int inputs = 3;
        const int outputs = 2;
        const int kernel = 3;
        const int padding = 1;
        float[] input = Values(height * width * inputs, seed: 1);
        float[] weight = Values(outputs * inputs * kernel * kernel, seed: 2);
        float[] bias = Values(outputs, seed: 3);
        var store = new MemoryTensorStore();
        store.Add("conv.weight", weight, outputs, inputs, kernel, kernel);
        store.Add("conv.bias", bias, outputs);
        using DirectConv2D convolution = DirectConv2D.FromTensorStore(_context, store, "conv.weight", "conv.bias");
        using TsTensor source = _context.FromFloats(input, 1, height, width, inputs);

        // A one-byte workspace computes one output row per im2col band.
        using TsTensor result = convolution.Forward(source, paddingHeight: padding, paddingWidth: padding,
            workspaceBytes: 1);

        Assert.Equal([1L, height, width, outputs], result.Sizes.ToArray());
        var expected = new float[height * width * outputs];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int output = 0; output < outputs; output++)
                {
                    float sum = bias[output];
                    for (int channel = 0; channel < inputs; channel++)
                    {
                        for (int kernelY = 0; kernelY < kernel; kernelY++)
                        {
                            for (int kernelX = 0; kernelX < kernel; kernelX++)
                            {
                                int sourceY = y - padding + kernelY;
                                int sourceX = x - padding + kernelX;
                                if (sourceY < 0 || sourceY >= height || sourceX < 0 || sourceX >= width)
                                {
                                    continue;
                                }

                                sum += weight[((output * inputs + channel) * kernel + kernelY) * kernel + kernelX]
                                    * input[(sourceY * width + sourceX) * inputs + channel];
                            }
                        }
                    }

                    expected[(y * width + x) * outputs + output] = sum;
                }
            }
        }

        AssertClose(expected, DirectOps.ToArray(result));
    }

    [Fact]
    public void GroupNormMatchesAManagedReference()
    {
        const int height = 2;
        const int width = 3;
        const int channels = 4;
        const int groups = 2;
        const float epsilon = 1e-6f;
        float[] input = Values(height * width * channels, seed: 4);
        float[] gain = Values(channels, seed: 5);
        float[] bias = Values(channels, seed: 6);
        using TsTensor source = _context.FromFloats(input, 1, height, width, channels);
        using TsTensor gainTensor = _context.FromFloats(gain, channels);
        using TsTensor biasTensor = _context.FromFloats(bias, channels);

        using TsTensor result = DirectImageOps.GroupNorm(_context, source, groups, gainTensor, biasTensor, epsilon);

        Assert.Equal([1L, height, width, channels], result.Sizes.ToArray());
        int perGroup = channels / groups;
        var expected = new float[input.Length];
        for (int group = 0; group < groups; group++)
        {
            int[] members = Enumerable.Range(0, height * width)
                .SelectMany(pixel => Enumerable.Range(group * perGroup, perGroup)
                    .Select(channel => pixel * channels + channel))
                .ToArray();
            double mean = members.Average(index => (double)input[index]);
            double variance = members.Average(index => Math.Pow(input[index] - mean, 2));
            foreach (int index in members)
            {
                int channel = index % channels;
                expected[index] = (float)((input[index] - mean) / Math.Sqrt(variance + epsilon)) * gain[channel]
                    + bias[channel];
            }
        }

        AssertClose(expected, DirectOps.ToArray(result));
    }

    [Fact]
    public void NearestUpsampleRepeatsEachPixel()
    {
        const int height = 2;
        const int width = 3;
        const int channels = 2;
        const int scale = 2;
        float[] input = Values(height * width * channels, seed: 7);
        using TsTensor source = _context.FromFloats(input, 1, height, width, channels);

        using TsTensor result = DirectImageOps.NearestUpsample2D(_context, source, scale);

        Assert.Equal([1L, height * scale, width * scale, channels], result.Sizes.ToArray());
        var expected = new float[input.Length * scale * scale];
        for (int y = 0; y < height * scale; y++)
        {
            for (int x = 0; x < width * scale; x++)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    expected[(y * width * scale + x) * channels + channel] =
                        input[(y / scale * width + x / scale) * channels + channel];
                }
            }
        }

        Assert.Equal(expected, DirectOps.ToArray(result));
    }

    public void Dispose() => _context.Dispose();

    private static float[] Values(int count, int seed) =>
        Enumerable.Range(0, count).Select(index => MathF.Sin(seed * 31f + index * 0.7f)).ToArray();

    private static void AssertClose(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], actual[index], Tolerance);
        }
    }

    private sealed class MemoryTensorStore : IFloatTensorStore
    {
        private readonly Dictionary<string, (float[] Values, long[] Shape)> _tensors = new(StringComparer.Ordinal);

        public void Add(string name, float[] values, params long[] shape) => _tensors.Add(name, (values, shape));

        public bool HasTensor(string name) => _tensors.ContainsKey(name);

        public float[] ReadFloat32(string name) => (float[])_tensors[name].Values.Clone();

        public long[] TensorShape(string name) =>
            _tensors.TryGetValue(name, out (float[] Values, long[] Shape) tensor) ? (long[])tensor.Shape.Clone() : [];
    }
}
