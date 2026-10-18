// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// Ported from TensorSharp.Models/Direct/DirectOps.cs. TensorSharp is licensed under the BSD-3-Clause license.

using TensorSharp;
using TensorSharp.Cuda;
using TsTensor = TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.Image.Supra.Direct;

/// <summary>
/// The allocator Supra's networks run on, managed CPU or CUDA, and the long-lived tensors (weights, cached gain
/// vectors) that are disposed with it. Ported from TensorSharp's internal <c>DirectContext</c>.
/// </summary>
internal sealed class DirectContext : IDisposable
{
    private readonly Dictionary<long, TsTensor> _ones = [];
    private readonly List<TsTensor> _owned = [];

    public DirectContext(IAllocator allocator)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        Allocator = allocator;
        CudaAllocator = allocator as CudaAllocator;
        IsCuda = CudaAllocator is not null;
    }

    public IAllocator Allocator { get; }

    public bool IsCuda { get; }

    public CudaAllocator? CudaAllocator { get; }

    public TsTensor NewF32(params long[] sizes) => new(Allocator, DType.Float32, sizes);

    /// <summary>Returns a cached all-ones gain vector of <paramref name="dim"/> values (LayerNorm without affine).</summary>
    public TsTensor Ones(long dim)
    {
        lock (_ones)
        {
            if (_ones.TryGetValue(dim, out TsTensor? cached))
            {
                return cached;
            }

            TsTensor ones = NewF32(dim);
            Ops.Fill(ones, 1f);
            _ones[dim] = ones;
            return ones;
        }
    }

    /// <summary>Tracks a long-lived tensor, such as a weight, for disposal with the context.</summary>
    public TsTensor Own(TsTensor tensor)
    {
        _owned.Add(tensor);
        return tensor;
    }

    public TsTensor FromFloats(float[] data, params long[] sizes)
    {
        TsTensor tensor = NewF32(sizes);
        tensor.SetElementsAsFloat(data);
        return tensor;
    }

    public void Dispose()
    {
        foreach (TsTensor tensor in _owned)
        {
            tensor.Dispose();
        }

        _owned.Clear();
        lock (_ones)
        {
            foreach (TsTensor tensor in _ones.Values)
            {
                tensor.Dispose();
            }

            _ones.Clear();
        }

        if (CudaAllocator is not null)
        {
            CudaQuantizedOps.ClearDeviceCache(CudaAllocator);
        }
    }
}
