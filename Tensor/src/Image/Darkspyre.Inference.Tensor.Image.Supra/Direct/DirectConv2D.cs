using TensorSharp;
using TensorSharp.Cpu;
using TensorSharp.Cuda;
using TensorSharp.Runtime;
using TsTensor = TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.Image.Supra.Direct;

/// <summary>
/// A channels-last 2D convolution: inputs and outputs are <c>[batch, height, width, channels]</c>, weights are in the
/// PyTorch layout <c>[out, in, kernelHeight, kernelWidth]</c>. It runs as banded im2col followed by a
/// <see cref="DirectLinear"/> over the flattened kernel. Ported from the TensorSharp fork's <c>DirectConv2D</c>.
/// </summary>
internal sealed class DirectConv2D : IDisposable
{
    private readonly DirectContext _context;
    private readonly DirectLinear _linear;
    private readonly int _inputChannels;
    private readonly int _outputChannels;
    private readonly int _kernelHeight;
    private readonly int _kernelWidth;

    private DirectConv2D(DirectContext context, DirectLinear linear, int inputChannels, int outputChannels,
        int kernelHeight, int kernelWidth)
    {
        _context = context;
        _linear = linear;
        _inputChannels = inputChannels;
        _outputChannels = outputChannels;
        _kernelHeight = kernelHeight;
        _kernelWidth = kernelWidth;
    }

    /// <summary>Loads a PyTorch-layout convolution weight and an optional <c>[out]</c> bias from a tensor store.</summary>
    public static DirectConv2D FromTensorStore(DirectContext context, IFloatTensorStore store, string weight,
        string? bias = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(weight);
        long[] shape = store.TensorShape(weight);
        if (shape.Length != 4 || Array.Exists(shape, value => value <= 0))
        {
            throw new InvalidOperationException(
                $"Direct convolution weight '{weight}' must have shape [out, in, kernelHeight, kernelWidth].");
        }

        if (Array.Exists(shape, value => value > int.MaxValue))
        {
            throw new NotSupportedException(
                $"Direct convolution weight '{weight}' has a dimension larger than Int32.MaxValue.");
        }

        float[]? biasValues = null;
        if (bias is not null && store.HasTensor(bias))
        {
            long[] biasShape = store.TensorShape(bias);
            if (biasShape.Length != 1 || biasShape[0] != shape[0])
            {
                throw new InvalidOperationException(
                    $"Direct convolution bias '{bias}' must have shape [{shape[0]}].");
            }

            biasValues = store.ReadFloat32(bias);
        }

        int outputChannels = checked((int)shape[0]);
        int inputChannels = checked((int)shape[1]);
        int kernelHeight = checked((int)shape[2]);
        int kernelWidth = checked((int)shape[3]);
        float[] values = store.ReadFloat32(weight);
        long flattened = checked((long)inputChannels * kernelHeight * kernelWidth);
        if (values.LongLength != checked(outputChannels * flattened))
        {
            throw new ArgumentException(
                $"Convolution weight length does not match [{outputChannels}, {inputChannels}, {kernelHeight}, {kernelWidth}].",
                nameof(weight));
        }

        return new DirectConv2D(context, DirectLinear.FromFloats(context, values, flattened, outputChannels, biasValues),
            inputChannels, outputChannels, kernelHeight, kernelWidth);
    }

    /// <summary>
    /// Applies the convolution. Padding and stride are symmetric per spatial axis. The im2col workspace is banded to
    /// <paramref name="workspaceBytes"/>.
    /// </summary>
    public TsTensor Forward(TsTensor input, int paddingHeight = 0, int paddingWidth = 0, int strideHeight = 1,
        int strideWidth = 1, long workspaceBytes = 256 << 20)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.DimensionCount != 4)
        {
            throw new ArgumentException("DirectConv2D input must have shape [batch, height, width, channels].",
                nameof(input));
        }

        if (input.ElementType != DType.Float32 || !input.IsContiguous())
        {
            throw new ArgumentException("DirectConv2D input must be contiguous F32.", nameof(input));
        }

        if (input.Sizes[3] != _inputChannels)
        {
            throw new ArgumentException(
                $"DirectConv2D expected {_inputChannels} channels but received {input.Sizes[3]}.", nameof(input));
        }

        if (!ReferenceEquals(input.Allocator, _context.Allocator))
        {
            throw new ArgumentException("Input must use the DirectContext allocator.", nameof(input));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(paddingHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(paddingWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(strideHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(strideWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workspaceBytes);

        long batch = input.Sizes[0];
        long height = input.Sizes[1];
        long width = input.Sizes[2];
        long outputHeight = (height + 2L * paddingHeight - _kernelHeight) / strideHeight + 1;
        long outputWidth = (width + 2L * paddingWidth - _kernelWidth) / strideWidth + 1;
        if (outputHeight <= 0 || outputWidth <= 0)
        {
            throw new ArgumentException("Convolution kernel is larger than the padded input.", nameof(input));
        }

        long columns = checked((long)_inputChannels * _kernelHeight * _kernelWidth);
        long bandHeight = Math.Max(1,
            Math.Min(outputHeight, workspaceBytes / Math.Max(1, checked(sizeof(float) * outputWidth * columns))));
        TsTensor output = _context.NewF32(batch, outputHeight, outputWidth, _outputChannels);
        try
        {
            for (long batchIndex = 0; batchIndex < batch; batchIndex++)
            {
                using TsTensor inputFrame = input.Select(0, batchIndex);
                using TsTensor outputFrame = output.Select(0, batchIndex);
                using TsTensor outputRows = outputFrame.View(outputHeight * outputWidth, _outputChannels);
                for (long outputY = 0; outputY < outputHeight; outputY += bandHeight)
                {
                    long rows = Math.Min(bandHeight, outputHeight - outputY);
                    using TsTensor columnRows = _context.NewF32(rows * outputWidth, columns);
                    Im2Col(columnRows, inputFrame, height, width, outputHeight, outputWidth, paddingHeight,
                        paddingWidth, strideHeight, strideWidth, outputY, rows);
                    using TsTensor projected = _linear.Forward(columnRows);
                    using TsTensor target = outputRows.Narrow(0, outputY * outputWidth, rows * outputWidth);
                    Ops.Copy(target, projected);
                }
            }

            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    public void Dispose() => _linear.Dispose();

    private void Im2Col(TsTensor columns, TsTensor input, long height, long width, long outputHeight,
        long outputWidth, int paddingHeight, int paddingWidth, int strideHeight, int strideWidth, long outputY,
        long bandHeight)
    {
        if (_context.IsCuda)
        {
            if (!CudaWanOps.TryIm2Col(columns, input, _inputChannels, checked((int)height), checked((int)width),
                    _kernelHeight, _kernelWidth, checked((int)outputHeight), checked((int)outputWidth), paddingHeight,
                    paddingWidth, strideHeight, strideWidth, checked((int)outputY), checked((int)bandHeight)))
            {
                throw new InvalidOperationException("CUDA im2col kernel unavailable (stale PTX?).");
            }

            return;
        }

        Im2ColCpu(columns, input, height, width, outputWidth, paddingHeight, paddingWidth, strideHeight, strideWidth,
            outputY, bandHeight);
    }

    private unsafe void Im2ColCpu(TsTensor columns, TsTensor input, long height, long width, long outputWidth,
        int paddingHeight, int paddingWidth, int strideHeight, int strideWidth, long outputY, long bandHeight)
    {
        float* source = (float*)CpuNativeHelpers.GetBufferStart(input);
        float* destination = (float*)CpuNativeHelpers.GetBufferStart(columns);
        long flattened = (long)_inputChannels * _kernelHeight * _kernelWidth;
        Parallel.For(0, checked((int)(bandHeight * outputWidth)), position =>
        {
            long oy = outputY + position / outputWidth;
            long ox = position % outputWidth;
            float* row = destination + position * flattened;
            long index = 0;
            for (int channel = 0; channel < _inputChannels; channel++)
            {
                for (int kernelY = 0; kernelY < _kernelHeight; kernelY++)
                {
                    long inputY = oy * strideHeight - paddingHeight + kernelY;
                    for (int kernelX = 0; kernelX < _kernelWidth; kernelX++, index++)
                    {
                        long inputX = ox * strideWidth - paddingWidth + kernelX;
                        row[index] = inputY >= 0 && inputY < height && inputX >= 0 && inputX < width
                            ? source[(inputY * width + inputX) * _inputChannels + channel]
                            : 0f;
                    }
                }
            }
        });
    }
}
