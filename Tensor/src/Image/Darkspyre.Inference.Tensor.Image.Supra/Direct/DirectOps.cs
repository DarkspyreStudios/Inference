// Copyright (c) Zhongkai Fu. All rights reserved.
// https://github.com/zhongkaifu/TensorSharp
//
// Ported from TensorSharp.Models/Direct/DirectOps.cs. TensorSharp is licensed under the BSD-3-Clause license.

using System.Numerics;
using TensorSharp;
using TensorSharp.Cpu;
using TensorSharp.Cuda;
using TsTensor = TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.Image.Supra.Direct;

/// <summary>
/// Backend-dispatched primitives for Supra's networks. CUDA uses TensorSharp's public CUDA kernels; CPU uses unsafe
/// managed loops and a parallel SIMD GEMM, because the generic elementwise ops do not broadcast a <c>[dim]</c> vector
/// over rows on every backend. Ported from TensorSharp's internal <c>DirectOps</c>.
/// </summary>
internal static class DirectOps
{
    /// <summary>RMS norm over the last dimension with a gain.</summary>
    public static TsTensor RmsNorm(DirectContext context, TsTensor input, TsTensor gain, float epsilon)
    {
        TsTensor result = context.NewF32(input.Sizes[0], input.Sizes[1]);
        // TensorSharp declares the bias non-nullable; null means no bias.
        Ops.RMSNorm(result, input, gain, null!, epsilon);
        return result;
    }

    /// <summary>LayerNorm over the last dimension; without <paramref name="gamma"/> and <paramref name="beta"/> it
    /// is not affine.</summary>
    public static TsTensor LayerNorm(DirectContext context, TsTensor input, TsTensor? gamma, TsTensor? beta,
        float epsilon)
    {
        TsTensor result = context.NewF32(input.Sizes[0], input.Sizes[1]);
        // TensorSharp declares beta non-nullable; null means no bias.
        Ops.LayerNorm(result, input, gamma ?? context.Ones(input.Sizes[1]), beta!, epsilon);
        return result;
    }

    public static TsTensor Silu(DirectContext context, TsTensor input)
    {
        TsTensor result = context.NewF32(input.Sizes.ToArray());
        Ops.SiLU(result, input);
        return result;
    }

    /// <summary>GELU, tanh approximation (as ggml_gelu).</summary>
    public static TsTensor Gelu(DirectContext context, TsTensor input)
    {
        TsTensor result = context.NewF32(input.Sizes.ToArray());
        Ops.GELU(result, input);
        return result;
    }

    /// <summary>
    /// Multi-head attention over token-major projections: <paramref name="query"/> <c>[sq, heads*headDim]</c>,
    /// <paramref name="key"/> and <paramref name="value"/> <c>[sk, heads*headDim]</c>, and an optional additive
    /// <paramref name="bias"/> <c>[heads, sq, sk]</c> added after <paramref name="scale"/>. Returns
    /// <c>[sq, heads*headDim]</c>. CUDA uses the streaming attention kernel, or the vision kernel for unbiased
    /// self-attention; large unbiased CUDA attention and every CPU attention run chunked per-head GEMM and softmax.
    /// </summary>
    public static TsTensor Attention(DirectContext context, TsTensor query, TsTensor key, TsTensor value, int heads,
        int headDim, float scale, TsTensor? bias = null)
    {
        int sq = checked((int)query.Sizes[0]);
        int sk = checked((int)key.Sizes[0]);
        TsTensor output = context.NewF32(sq, (long)heads * headDim);

        // The streaming F32 kernel is launch-lean but scalar (about 1.3 TFLOPS); the chunked cuBLAS GEMM and softmax
        // path reaches about 4.7 TFLOPS but pays copies and scratch. The measured crossover is a total workload of
        // roughly 100 GFLOP, so big unbiased attentions take the GEMM path.
        long attentionFlop = 4L * sq * sk * heads * headDim;
        bool preferGemm = context.IsCuda && bias is null && attentionFlop > 100_000_000_000L;
        if (!preferGemm && context.IsCuda
            && CudaWanOps.TryAttention(output, query, key, value, bias, heads, sq, sk, headDim, scale))
        {
            return output;
        }

        if (!preferGemm && context.IsCuda && bias is null && sq == sk
            && CudaFusedOps.TryVisionAttention(output, query, key, value, heads, sq, headDim, scale))
        {
            return output;
        }

        GenericAttention(context, output, query, key, value, bias, heads, sq, sk, headDim, scale);
        return output;
    }

    /// <summary>AdaLN modulation <c>y[r, c] = x[r, c] * (1 + scale[c]) + shift[c]</c> over rows
    /// <c>[rowFrom, rowFrom + rowCount)</c>. <paramref name="output"/> and <paramref name="input"/> may alias.</summary>
    public static void ModulateRows(DirectContext context, TsTensor output, TsTensor input, TsTensor shift,
        TsTensor scale, long rowFrom, long rowCount)
    {
        long columns = input.Sizes[1];
        if (context.IsCuda)
        {
            if (!CudaWanOps.TryModulateRows(output, input, shift, scale, rowFrom, rowCount))
            {
                throw new InvalidOperationException("CUDA ModulateRows kernel unavailable (stale PTX?).");
            }

            return;
        }

        unsafe
        {
            float* pointerInput = (float*)CpuNativeHelpers.GetBufferStart(input);
            float* pointerOutput = (float*)CpuNativeHelpers.GetBufferStart(output);
            float* pointerShift = (float*)CpuNativeHelpers.GetBufferStart(shift);
            float* pointerScale = (float*)CpuNativeHelpers.GetBufferStart(scale);
            RowsParallel(rowCount, relativeRow =>
            {
                long row = rowFrom + relativeRow;
                float* rowInput = pointerInput + row * columns;
                float* rowOutput = pointerOutput + row * columns;
                for (long column = 0; column < columns; column++)
                {
                    rowOutput[column] = rowInput[column] * (1f + pointerScale[column]) + pointerShift[column];
                }
            });
        }
    }

    /// <summary>Gated residual <c>x[r, c] += v[r, c] * gate[c]</c> over rows
    /// <c>[rowFrom, rowFrom + rowCount)</c>.</summary>
    public static void GateAddRows(DirectContext context, TsTensor input, TsTensor values, TsTensor gate,
        long rowFrom, long rowCount)
    {
        long columns = input.Sizes[1];
        if (context.IsCuda)
        {
            if (!CudaWanOps.TryGateAddRows(input, values, gate, rowFrom, rowCount))
            {
                throw new InvalidOperationException("CUDA GateAddRows kernel unavailable (stale PTX?).");
            }

            return;
        }

        unsafe
        {
            float* pointerInput = (float*)CpuNativeHelpers.GetBufferStart(input);
            float* pointerValues = (float*)CpuNativeHelpers.GetBufferStart(values);
            float* pointerGate = (float*)CpuNativeHelpers.GetBufferStart(gate);
            RowsParallel(rowCount, relativeRow =>
            {
                long row = rowFrom + relativeRow;
                float* rowInput = pointerInput + row * columns;
                float* rowValues = pointerValues + row * columns;
                for (long column = 0; column < columns; column++)
                {
                    rowInput[column] += rowValues[column] * pointerGate[column];
                }
            });
        }
    }

    /// <summary>Copies an F32 tensor's elements into a managed array.</summary>
    public static float[] ToArray(TsTensor tensor)
    {
        long count = 1;
        foreach (long size in tensor.Sizes)
        {
            count *= size;
        }

        return tensor.GetElementsAsFloat(checked((int)count));
    }

    /// <summary><c>t[r, c] += bias[c]</c> for every row. Used by <see cref="DirectLinear"/> and
    /// <see cref="DirectImageOps.GroupNorm"/>.</summary>
    internal static void AddBiasRows(DirectContext context, TsTensor tensor, TsTensor bias)
    {
        if (context.IsCuda)
        {
            if (!CudaFusedOps.TryAddBiasRows(tensor, bias))
            {
                throw new InvalidOperationException("CUDA AddBiasRows failed (non-contiguous tensor?).");
            }

            return;
        }

        unsafe
        {
            long rows = tensor.Sizes[0];
            long columns = tensor.Sizes[1];
            float* pointerTensor = (float*)CpuNativeHelpers.GetBufferStart(tensor);
            float* pointerBias = (float*)CpuNativeHelpers.GetBufferStart(bias);
            RowsParallel(rows, row =>
            {
                float* rowTensor = pointerTensor + row * columns;
                for (long column = 0; column < columns; column++)
                {
                    rowTensor[column] += pointerBias[column];
                }
            });
        }
    }

    /// <summary><c>x[r, c] *= gain[c]</c> for every row. Used by <see cref="DirectImageOps.GroupNorm"/>.</summary>
    internal static void MulColsRows(DirectContext context, TsTensor tensor, TsTensor gain)
    {
        if (context.IsCuda)
        {
            if (!CudaWanOps.TryScaleCols(tensor, gain))
            {
                throw new InvalidOperationException("CUDA ScaleCols kernel unavailable (stale PTX?).");
            }

            return;
        }

        unsafe
        {
            long rows = tensor.Sizes[0];
            long columns = tensor.Sizes[1];
            float* pointerTensor = (float*)CpuNativeHelpers.GetBufferStart(tensor);
            float* pointerGain = (float*)CpuNativeHelpers.GetBufferStart(gain);
            RowsParallel(rows, row =>
            {
                float* rowTensor = pointerTensor + row * columns;
                for (long column = 0; column < columns; column++)
                {
                    rowTensor[column] *= pointerGain[column];
                }
            });
        }
    }

    /// <summary><c>C[m, n] = beta * C + alpha * A[m, k] x B[n, k]^T</c>, both row-major; the row stride of
    /// <paramref name="c"/> comes from its strides. Used by <see cref="DirectLinear"/>.</summary>
    internal static unsafe void CpuGemmABt(TsTensor a, TsTensor b, TsTensor c, float alpha, float beta)
    {
        long m = a.Sizes[0];
        long k = a.Sizes[1];
        long n = b.Sizes[0];
        long aStride = a.Strides[0];
        long cStride = c.Strides[0];
        float* pointerA = (float*)CpuNativeHelpers.GetBufferStart(a);
        float* pointerB = (float*)CpuNativeHelpers.GetBufferStart(b);
        float* pointerC = (float*)CpuNativeHelpers.GetBufferStart(c);
        int width = Vector<float>.Count;
        RowsParallel(m, i =>
        {
            float* rowA = pointerA + i * aStride;
            float* rowC = pointerC + i * cStride;
            for (long j = 0; j < n; j++)
            {
                float* rowB = pointerB + j * k;
                Vector<float> accumulator = Vector<float>.Zero;
                long t = 0;
                for (; t + width <= k; t += width)
                {
                    accumulator += new Vector<float>(new ReadOnlySpan<float>(rowA + t, width))
                        * new Vector<float>(new ReadOnlySpan<float>(rowB + t, width));
                }

                float sum = Vector.Dot(accumulator, Vector<float>.One);
                for (; t < k; t++)
                {
                    sum += rowA[t] * rowB[t];
                }

                sum *= alpha;
                rowC[j] = beta == 0f ? sum : beta * rowC[j] + sum;
            }
        });
    }

    /// <summary>Chunked per-head GEMM and softmax: cuBLAS through <c>Ops.Addmm</c> on CUDA, the parallel SIMD
    /// kernels on CPU. CUDA takes bigger chunks for fewer launches.</summary>
    private static void GenericAttention(DirectContext context, TsTensor output, TsTensor query, TsTensor key,
        TsTensor value, TsTensor? bias, int heads, int sq, int sk, int headDim, float scale)
    {
        // Score elements per query chunk.
        long budget = context.IsCuda ? 64L << 20 : 16L << 20;
        int queryChunk = (int)Math.Max(1, Math.Min(sq, budget / Math.Max(1, sk)));

        for (int head = 0; head < heads; head++)
        {
            using TsTensor keyNarrow = key.Narrow(1, (long)head * headDim, headDim);
            using TsTensor keyHead = Ops.NewContiguous(keyNarrow);
            using TsTensor keyHeadTransposed = keyHead.Transpose();
            using TsTensor valueNarrow = value.Narrow(1, (long)head * headDim, headDim);
            using TsTensor valueHead = Ops.NewContiguous(valueNarrow);
            for (int queryStart = 0; queryStart < sq; queryStart += queryChunk)
            {
                int queryCount = Math.Min(queryChunk, sq - queryStart);
                using TsTensor queryRows = query.Narrow(0, queryStart, queryCount);
                using TsTensor queryNarrow = queryRows.Narrow(1, (long)head * headDim, headDim);
                using TsTensor queryHead = Ops.NewContiguous(queryNarrow);
                using TsTensor scores = context.NewF32(queryCount, sk);
                if (context.IsCuda)
                {
                    Ops.Addmm(scores, 0f, scores, scale, queryHead, keyHeadTransposed);
                }
                else
                {
                    CpuGemmABt(queryHead, keyHead, scores, scale, 0f);
                }

                if (bias is not null)
                {
                    using TsTensor biasHead = bias.Select(0, head);
                    using TsTensor biasRows = biasHead.Narrow(0, queryStart, queryCount);
                    Ops.Add(scores, scores, biasRows);
                }

                Ops.Softmax(scores, scores);
                using TsTensor headOutput = context.NewF32(queryCount, headDim);
                if (context.IsCuda)
                {
                    Ops.Addmm(headOutput, 0f, headOutput, 1f, scores, valueHead);
                }
                else
                {
                    CpuGemmAB(scores, valueHead, headOutput, 1f, 0f);
                }

                using TsTensor outputRows = output.Narrow(0, queryStart, queryCount);
                using TsTensor destination = outputRows.Narrow(1, (long)head * headDim, headDim);
                Ops.Copy(destination, headOutput);
            }
        }
    }

    /// <summary><c>C[m, n] = beta * C + alpha * A[m, k] x B[k, n]</c>, both row-major.</summary>
    private static unsafe void CpuGemmAB(TsTensor a, TsTensor b, TsTensor c, float alpha, float beta)
    {
        long m = a.Sizes[0];
        long k = a.Sizes[1];
        long n = b.Sizes[1];
        long aStride = a.Strides[0];
        long cStride = c.Strides[0];
        float* pointerA = (float*)CpuNativeHelpers.GetBufferStart(a);
        float* pointerB = (float*)CpuNativeHelpers.GetBufferStart(b);
        float* pointerC = (float*)CpuNativeHelpers.GetBufferStart(c);
        int width = Vector<float>.Count;
        RowsParallel(m, i =>
        {
            float* rowA = pointerA + i * aStride;
            float* rowC = pointerC + i * cStride;
            if (beta == 0f)
            {
                new Span<float>(rowC, checked((int)n)).Clear();
            }
            else if (beta != 1f)
            {
                for (long j = 0; j < n; j++)
                {
                    rowC[j] *= beta;
                }
            }

            for (long t = 0; t < k; t++)
            {
                float scaled = rowA[t] * alpha;
                if (scaled == 0f)
                {
                    continue;
                }

                float* rowB = pointerB + t * n;
                var broadcast = new Vector<float>(scaled);
                long j = 0;
                for (; j + width <= n; j += width)
                {
                    var current = new Vector<float>(new ReadOnlySpan<float>(rowC + j, width));
                    var addend = new Vector<float>(new ReadOnlySpan<float>(rowB + j, width));
                    (current + broadcast * addend).CopyTo(new Span<float>(rowC + j, width));
                }

                for (; j < n; j++)
                {
                    rowC[j] += scaled * rowB[j];
                }
            }
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> for each row, in contiguous chunks of rows, a few per processor, so the tail still
    /// balances. One call is often only tens of microseconds of work per core, so one row or one processor runs inline.
    /// </summary>
    private static void RowsParallel(long rows, Action<long> body)
    {
        if (rows <= 0)
        {
            return;
        }

        int threads = Environment.ProcessorCount;
        if (rows == 1 || threads <= 1)
        {
            for (long row = 0; row < rows; row++)
            {
                body(row);
            }

            return;
        }

        long chunk = Math.Max(1, (rows + threads * 4L - 1) / (threads * 4L));
        int blocks = checked((int)((rows + chunk - 1) / chunk));
        Parallel.For(0, blocks, block =>
        {
            long start = block * chunk;
            long end = Math.Min(rows, start + chunk);
            for (long row = start; row < end; row++)
            {
                body(row);
            }
        });
    }
}
