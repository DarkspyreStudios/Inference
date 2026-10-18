// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// Ported from TensorSharp.Models/Direct/DirectOps.cs. TensorSharp is licensed under the BSD-3-Clause license.

using System.Runtime.InteropServices;
using TensorSharp.Cuda;
using TensorSharp.Runtime;
using TsTensor = TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.Image.Supra.Direct;

/// <summary>
/// One linear layer, <c>y = x W^T (+ bias)</c>, over F32 weights. On CUDA the weight is a stable host copy behind
/// TensorSharp's resident quantized-weight cache, keyed by its host address; on CPU it is a context-owned
/// <c>[out, in]</c> tensor fed to the parallel managed GEMM. Ported from TensorSharp's internal <c>DirectLinear</c>.
/// </summary>
internal sealed class DirectLinear : IDisposable
{
    // The ggml type id of F32, the only storage type Supra's weights reach this layer in.
    private const int Float32Type = 0;

    private readonly DirectContext _context;
    private readonly IntPtr _host;
    private readonly long _bytes;
    private readonly TsTensor? _cpuWeight;
    private readonly TsTensor? _bias;
    private bool _released;

    private DirectLinear(DirectContext context, IntPtr host, long bytes, TsTensor? cpuWeight, TsTensor? bias,
        long inDim, long outDim)
    {
        _context = context;
        _host = host;
        _bytes = bytes;
        _cpuWeight = cpuWeight;
        _bias = bias;
        InDim = inDim;
        OutDim = outDim;
    }

    public long InDim { get; }

    public long OutDim { get; }

    /// <summary>Loads a row-major <c>[out, in]</c> weight and an optional <c>[out]</c> bias from a tensor store.</summary>
    public static DirectLinear FromTensorStore(DirectContext context, IFloatTensorStore store, string weight,
        string? bias = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(weight);
        long[] shape = store.TensorShape(weight);
        if (shape.Length != 2 || shape[0] <= 0 || shape[1] <= 0)
        {
            throw new InvalidOperationException($"Direct linear weight '{weight}' must have shape [out, in].");
        }

        float[]? biasValues = null;
        if (bias is not null && store.HasTensor(bias))
        {
            long[] biasShape = store.TensorShape(bias);
            if (biasShape.Length != 1 || biasShape[0] != shape[0])
            {
                throw new InvalidOperationException($"Direct linear bias '{bias}' must have shape [{shape[0]}].");
            }

            biasValues = store.ReadFloat32(bias);
        }

        return FromFloats(context, store.ReadFloat32(weight), shape[1], shape[0], biasValues);
    }

    /// <summary>Builds the layer from a row-major <c>[outDim, inDim]</c> weight; <see cref="DirectConv2D"/> passes its
    /// flattened convolution kernel here.</summary>
    internal static DirectLinear FromFloats(DirectContext context, float[] weight, long inDim, long outDim,
        float[]? bias)
    {
        TsTensor? biasTensor = bias is null ? null : context.Own(context.FromFloats(bias, bias.LongLength));
        if (context.IsCuda)
        {
            long bytes = weight.LongLength * sizeof(float);
            IntPtr host = Marshal.AllocHGlobal(checked((IntPtr)bytes));
            Marshal.Copy(weight, 0, host, weight.Length);
            return new DirectLinear(context, host, bytes, null, biasTensor, inDim, outDim);
        }

        TsTensor cpuWeight = context.Own(context.FromFloats(weight, outDim, inDim));
        return new DirectLinear(context, IntPtr.Zero, 0, cpuWeight, biasTensor, inDim, outDim);
    }

    /// <summary>Projects a contiguous F32 <c>[rows, InDim]</c> input into a new <c>[rows, OutDim]</c> tensor.</summary>
    public TsTensor Forward(TsTensor input)
    {
        TsTensor result = _context.NewF32(input.Sizes[0], OutDim);
        if (_cpuWeight is null)
        {
            if (!CudaQuantizedOps.TryAddmmQuantizedToFloat32(result, input, _host, _host, Float32Type, InDim, OutDim,
                    _bytes))
            {
                throw new InvalidOperationException(
                    $"CUDA linear failed ([{input.Sizes[0]},{InDim}]x[{InDim},{OutDim}]).");
            }
        }
        else
        {
            DirectOps.CpuGemmABt(input, _cpuWeight, result, 1f, 0f);
        }

        if (_bias is not null)
        {
            DirectOps.AddBiasRows(_context, result, _bias);
        }

        return result;
    }

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        if (_host != IntPtr.Zero)
        {
            if (_context.CudaAllocator is not null)
            {
                CudaQuantizedOps.ReleaseQuantizedWeight(_context.CudaAllocator, _host);
            }

            Marshal.FreeHGlobal(_host);
        }
    }
}
