using System.Diagnostics;
using System.Text.Json;
using TensorSharp;
using TensorSharp.GGML;
using TsTensor = TensorSharp.Tensor;

namespace Darkspyre.Inference.Tensor.GemmaExperiment;

internal enum MetalVariant { Baseline, Resident, Fused, Grouped }

// Experimental forward pass for the pinned ONNX export's FP32 weights. This is not a
// registered Tensor engine. Frozen inputs isolate the runtime from tokenizer differences.
internal sealed class MetalText : IDisposable
{
    private readonly GgmlContext context;
    private readonly GgmlAllocator allocator;
    private readonly Dictionary<string, TsTensor> weights = new(StringComparer.Ordinal);
    private readonly MetalVariant variant;
    private bool ResidentWeights => variant != MetalVariant.Baseline;
    private bool Fused => variant is MetalVariant.Fused or MetalVariant.Grouped;

    private MetalText(string root, MetalVariant variant)
    {
        this.variant = variant;
        context = new GgmlContext([0], GgmlBackendType.Metal);
        allocator = new GgmlAllocator(context, 0);
        try
        {
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "weights.json")));
            using var stream = File.OpenRead(Path.Combine(root, "model.onnx_data"));
            foreach (JsonProperty property in metadata.RootElement.EnumerateObject())
            {
                if (!property.Name.StartsWith("model.", StringComparison.Ordinal)) continue;
                JsonElement item = property.Value;
                if (item.GetProperty("type").GetInt32() != 1)
                    throw new InvalidDataException($"Expected FP32 initializer {property.Name}");
                long[] shape = [.. item.GetProperty("shape").EnumerateArray().Select(value => value.GetInt64())];
                int count = checked((int)shape.Aggregate(1L, (a, b) => a * b));
                byte[] bytes = new byte[checked(count * sizeof(float))];
                JsonElement external = item.GetProperty("external");
                if (external.TryGetProperty("offset", out JsonElement offset))
                {
                    stream.Position = long.Parse(offset.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                    stream.ReadExactly(bytes);
                }
                else
                {
                    byte[] raw = [.. item.GetProperty("raw").EnumerateArray().Select(value => value.GetByte())];
                    if (raw.Length != bytes.Length) throw new InvalidDataException($"Invalid initializer {property.Name}");
                    raw.CopyTo(bytes, 0);
                }
                float[] values = new float[count];
                Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
                if (ResidentWeights && property.Name.EndsWith(".MatMul.weight", StringComparison.Ordinal))
                {
                    // GGML consumes contiguous [output, input] weights. Pack once during
                    // loading so AddmmQuant(F32) can retain the same native binding.
                    int input = checked((int)shape[0]);
                    int output = checked((int)shape[1]);
                    float[] packed = new float[count];
                    for (int i = 0; i < input; i++)
                        for (int o = 0; o < output; o++) packed[o * input + i] = values[i * output + o];
                    values = packed;
                    shape = [output, input];
                }
                var tensor = new TsTensor(allocator, DType.Float32, shape);
                weights.Add(property.Name, tensor);
                tensor.CopyFrom(values);
            }
        }
        catch { Dispose(); throw; }
    }

    internal static int Run(string root, string output, long[] ids, MetalVariant variant)
    {
        IReadOnlyList<GgmlNativeCandidate> candidates = GgmlNativeLoader.ResolvePackageCandidatesAsync().GetAwaiter().GetResult();
        GgmlNativeLoader.Configure(new GgmlRuntimePlan(GgmlNativeLoader.TensorSharpBuild,
            GgmlNativeLoader.NativeAbi, GgmlNativeLoader.RuntimeIdentifier, candidates,
            RequestedBackend: GgmlBackendType.Metal));
        var initialized = GgmlNativeLoader.InitializeAsync().GetAwaiter().GetResult();
        Console.WriteLine($"GGML: {initialized.State}, actual backend {initialized.ActualBackend}");
        if (initialized.State != GgmlInitializationState.Ready || initialized.ActualBackend != GgmlBackendType.Metal)
            throw new InvalidOperationException(initialized.Diagnostic ?? "Metal was not selected.");
        bool released;
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            using var model = new MetalText(root, variant);
            double loadMs = watch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Loaded Metal weights in {loadMs:F1} ms");
            var times = new List<double>();
            var vectors = new List<float[]>();
            float[] vector = [];
            for (int i = 0; i < 6; i++)
            {
                watch.Restart();
                vector = model.Forward(ids);
                times.Add(watch.Elapsed.TotalMilliseconds);
                vectors.Add(vector);
                if (vector.Length != 768 || vector.Any(value => !float.IsFinite(value)))
                    throw new InvalidDataException("Metal did not return 768 finite values.");
                Console.WriteLine($"Run {i}: {times[^1]:F1} ms; norm {Math.Sqrt(vector.Sum(value => (double)value * value)):F6}");
            }
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                provider = "TensorSharp Metal",
                variant = variant.ToString(),
                inputIds = ids,
                loadMs,
                timesMs = times,
                vector,
                vectors,
                peakWorkingSetBytes = Program.PeakWorkingSet(),
                nativeAbi = GgmlNativeLoader.NativeAbi,
                note = "Experimental FP32 text forward; no tokenizer or retrieval qualification."
            }));
        }
        finally
        {
            var shutdown = GgmlNativeLoader.Shutdown();
            released = shutdown.Released;
            Console.WriteLine($"GGML shutdown: {shutdown.Released} {shutdown.Diagnostic}");
        }
        return released ? 0 : 1;
    }

    private float[] Forward(long[] ids)
    {
        int count = ids.Length;
        using var request = new Tensors();
        var indices = request.Add(new TsTensor(allocator, DType.Int32, count));
        indices.CopyFrom(ids.Select(value => checked((int)value)).ToArray());
        TsTensor hidden = Ops.IndexSelect(null!, W("embed_tokens.weight"), indices);
        try
        {
            // The export folds sqrt(hidden_size) into the embedding table and the PLE
            // projection scale into its normalization epsilon. Reapplying either changes it.
            TsTensor ple = Linear(request, hidden, "ple.per_layer_model_projection.MatMul.weight");
            TsTensor pleRows = request.Add(ple.View(count * 24, 512));
            TsTensor pleNormalized = Norm(request, pleRows, "ple.per_layer_projection_norm.weight", 0.000512f);
            TsTensor pleLayers = request.Add(pleNormalized.View(count, 24 * 512));
            for (int layer = 0; layer < 24; layer++)
            {
                using var tensors = new Tensors();
                string prefix = $"layers.{layer}.";
                int dimension = layer % 6 == 5 ? 512 : 256;
                int kvHeads = layer % 6 == 5 ? 1 : 2;
                int width = dimension * (4 + 2 * kvHeads);
                TsTensor qkv = NormLinear(tensors, hidden, prefix + "input_layernorm.weight", prefix + "attn.qkv_proj.MatMul.weight");
                TsTensor qkvRows = tensors.Add(qkv.View(count * (4 + 2 * kvHeads), dimension));
                TsTensor unitHeads = Norm(tensors, qkvRows, $"ones_{dimension}");
                TsTensor unitPacked = tensors.Add(unitHeads.View(count, width));
                TsTensor qkvWeighted = tensors.Add(Ops.Mul(null!, unitPacked, W(prefix + "attn.qkv_norm.weight")));
                TsTensor query = Slice(tensors, qkvWeighted, 0, 4 * dimension);
                TsTensor key = Slice(tensors, qkvWeighted, 4 * dimension, kvHeads * dimension);
                TsTensor value = Slice(tensors, qkvWeighted, (4 + kvHeads) * dimension, kvHeads * dimension);
                Rotate(tensors, query, count, 4, dimension);
                Rotate(tensors, key, count, kvHeads, dimension);
                TsTensor flatAttention;
                if (variant == MetalVariant.Grouped)
                {
                    TsTensor queries = HeadFirst(tensors, query, count, 4, dimension);
                    TsTensor keys = HeadFirst(tensors, key, count, kvHeads, dimension);
                    TsTensor values = HeadFirst(tensors, value, count, kvHeads, dimension);
                    flatAttention = tensors.Add(new TsTensor(allocator, DType.Float32, count, 4 * dimension));
                    // Exact extents and head-first FP32 inputs avoid padding and F16 flash
                    // conversion. Every key is visible for this <=512-token unpadded probe.
                    GgmlBasicOps.FusedPrefillAttention(queries, keys, values, flatAttention,
                        4, kvHeads, dimension, count, count, maskStartPos: count - 1,
                        slidingWindow: 0, scale: 1f, inputFormat: 0, matchPerOpPrecision: true);
                }
                else
                {
                    // Generic SDPA requires equal Q/K/V head counts.
                    TsTensor keys = RepeatHeads(tensors, key, count, kvHeads, dimension);
                    TsTensor values = RepeatHeads(tensors, value, count, kvHeads, dimension);
                    TsTensor queries = tensors.Add(query.View(1, count, 4, dimension));
                    TsTensor attention = tensors.Add(Ops.ScaledDotProductAttention(null!, queries, keys, values, null!, 1.0f));
                    flatAttention = tensors.Add(attention.View(count, 4 * dimension));
                }
                TsTensor projected = Linear(tensors, flatAttention, prefix + "attn.o_proj.MatMul.weight");
                if (Fused)
                {
                    GgmlBasicOps.RmsNormResidualAdd(hidden, projected, W(prefix + "post_attention_layernorm.weight"), 1e-6f);
                    FusedFeedForward(tensors, hidden, prefix);
                    TsTensor layerPle = Slice(tensors, pleLayers, layer * 512, 512);
                    FusedPle(hidden, layerPle, prefix);
                }
                else
                {
                    TsTensor postAttention = Norm(tensors, projected, prefix + "post_attention_layernorm.weight");
                    TsTensor residual = tensors.Add(Ops.Add(null!, hidden, postAttention));
                    TsTensor preMlp = Norm(tensors, residual, prefix + "pre_feedforward_layernorm.weight");
                    TsTensor gateUp = Linear(tensors, preMlp, prefix + "mlp.gate_up_proj.MatMul.weight");
                    TsTensor gate = Slice(tensors, gateUp, 0, 2048);
                    TsTensor up = Slice(tensors, gateUp, 2048, 2048);
                    TsTensor gated = tensors.Add(Ops.GELUMul(null!, gate, up));
                    TsTensor down = Linear(tensors, gated, prefix + "mlp.down_proj.MatMul.weight");
                    TsTensor postMlp = Norm(tensors, down, prefix + "post_feedforward_layernorm.weight");
                    TsTensor afterMlp = tensors.Add(Ops.Add(null!, residual, postMlp));
                    TsTensor pleGate = Linear(tensors, afterMlp, prefix + "ple.per_layer_input_gate.MatMul.weight");
                    TsTensor layerPle = Slice(tensors, pleLayers, layer * 512, 512);
                    TsTensor gatedPle = tensors.Add(Ops.GELUMul(null!, pleGate, layerPle));
                    TsTensor projectedPle = Linear(tensors, gatedPle, prefix + "ple.per_layer_projection.MatMul.weight");
                    TsTensor postPle = Norm(tensors, projectedPle, prefix + "ple.post_per_layer_input_norm.weight");
                    TsTensor next = Ops.Add(null!, afterMlp, postPle);
                    hidden.Dispose();
                    hidden = next;
                }
                // The export absorbs the final layer scalar into the final RMS epsilon.
                if (layer < 23) Ops.Mul(hidden, hidden, W(prefix + "layer_scalar").GetElementsAsFloat(1)[0]);
            }
            TsTensor projectedOutput = NormLinear(request, hidden, "norm.weight", "embedding_projection.MatMul.weight", 7.59493705118075e-05f);
            float[] rows = projectedOutput.GetElementsAsFloat(count * 768);
            float[] result = new float[768];
            for (int token = 0; token < count; token++)
                for (int i = 0; i < 768; i++) result[i] += rows[token * 768 + i] / count;
            double norm = Math.Sqrt(result.Sum(value => (double)value * value));
            for (int i = 0; i < result.Length; i++) result[i] = (float)(result[i] / Math.Max(norm, 1e-12));
            return result;
        }
        finally { hidden.Dispose(); }
    }

    private TsTensor W(string name) => weights["model." + name];
    private TsTensor Linear(Tensors tensors, TsTensor input, string name)
    {
        TsTensor weight = W(name);
        TsTensor result = tensors.Add(new TsTensor(allocator, DType.Float32, input.Sizes[0], weight.Sizes[ResidentWeights ? 0 : 1]));
        if (!ResidentWeights) return Ops.Addmm(result, 0f, result, 1f, input, weight);
        GgmlBasicOps.AddmmQuant(result, input, weight.Storage.PtrAtElement(weight.StorageOffset),
            0, weight.Sizes[1], weight.Sizes[0], weight.ElementCount() * sizeof(float));
        return result;
    }
    private TsTensor NormLinear(Tensors tensors, TsTensor input, string norm, string matrix, float epsilon = 1e-6f)
    {
        if (!Fused) return Linear(tensors, Norm(tensors, input, norm, epsilon), matrix);
        TsTensor weight = W(matrix);
        TsTensor result = tensors.Add(new TsTensor(allocator, DType.Float32, input.Sizes[0], weight.Sizes[0]));
        GgmlBasicOps.FusedRmsNormMatMulQuant(result, input, W(norm), epsilon,
            weight.Storage.PtrAtElement(weight.StorageOffset), 0, weight.Sizes[1], weight.Sizes[0], weight.ElementCount() * sizeof(float));
        return result;
    }

    private void FusedFeedForward(Tensors tensors, TsTensor hidden, string prefix)
    {
        TsTensor gateUp = W(prefix + "mlp.gate_up_proj.MatMul.weight");
        TsTensor down = W(prefix + "mlp.down_proj.MatMul.weight");
        TsTensor projected = tensors.Add(new TsTensor(allocator, DType.Float32, hidden.Sizes));
        GgmlBasicOps.FusedFFNActProjectQuant(projected, hidden, W(prefix + "pre_feedforward_layernorm.weight"), 1e-6f,
            gateUp.Storage.PtrAtElement(gateUp.StorageOffset), 0, gateUp.Sizes[1], gateUp.Sizes[0], gateUp.ElementCount() * sizeof(float),
            down.Storage.PtrAtElement(down.StorageOffset), 0, down.Sizes[1], down.Sizes[0], down.ElementCount() * sizeof(float),
            halfDim: 2048, actType: 1);
        GgmlBasicOps.RmsNormResidualAdd(hidden, projected, W(prefix + "post_feedforward_layernorm.weight"), 1e-6f);
    }

    private void FusedPle(TsTensor hidden, TsTensor layerPle, string prefix)
    {
        TsTensor gate = W(prefix + "ple.per_layer_input_gate.MatMul.weight");
        TsTensor projection = W(prefix + "ple.per_layer_projection.MatMul.weight");
        GgmlBasicOps.FusedPleBlockQuant(hidden, layerPle,
            gate.Storage.PtrAtElement(gate.StorageOffset), 0, gate.Sizes[1], gate.Sizes[0], gate.ElementCount() * sizeof(float),
            projection.Storage.PtrAtElement(projection.StorageOffset), 0, projection.Sizes[1], projection.Sizes[0], projection.ElementCount() * sizeof(float),
            W(prefix + "ple.post_per_layer_input_norm.weight"), 1e-6f);
    }

    private TsTensor Norm(Tensors tensors, TsTensor input, string name, float epsilon = 1e-6f) =>
        tensors.Add(Ops.RMSNorm(null!, input, W(name), null!, epsilon));

    private static TsTensor Slice(Tensors tensors, TsTensor input, int start, int length)
    {
        TsTensor view = tensors.Add(input.Narrow(1, start, length));
        return tensors.Add(Ops.NewContiguous(view));
    }

    private TsTensor RepeatHeads(Tensors tensors, TsTensor input, int count, int heads, int dimension)
    {
        TsTensor source = tensors.Add(input.View(count, heads, dimension));
        TsTensor target = tensors.Add(new TsTensor(allocator, DType.Float32, count, 4, dimension));
        for (int i = 0; i < 4; i++)
        {
            using TsTensor from = source.Narrow(1, i / (4 / heads), 1);
            using TsTensor to = target.Narrow(1, i, 1);
            Ops.Copy(to, from);
        }
        return tensors.Add(target.View(1, count, 4, dimension));
    }

    private static TsTensor HeadFirst(Tensors tensors, TsTensor input, int count, int heads, int dimension)
    {
        TsTensor shaped = tensors.Add(input.View(count, heads, dimension));
        TsTensor transposed = tensors.Add(shaped.Transpose(0, 1));
        return tensors.Add(Ops.NewContiguous(transposed));
    }

    private void Rotate(Tensors tensors, TsTensor input, int count, int heads, int dimension)
    {
        TsTensor positions = tensors.Add(new TsTensor(allocator, DType.Int32, count * heads));
        positions.CopyFrom(Enumerable.Range(0, count).SelectMany(i => Enumerable.Repeat(i, heads)).ToArray());
        TsTensor shaped = tensors.Add(input.View(1, count, heads, dimension));
        Ops.RoPEEx(shaped, shaped, positions, dimension, 2, 0, dimension == 512 ? 1000000f : 10000f, 1f);
    }

    /// <summary>Releases the model weights before the native context.</summary>
    public void Dispose()
    {
        foreach (TsTensor tensor in weights.Values)
        {
            if (ResidentWeights) GgmlBasicOps.InvalidateTensorHostBuffer(tensor);
            tensor.Dispose();
        }
        weights.Clear();
        context.Dispose();
    }

    private sealed class Tensors : IDisposable
    {
        private readonly List<TsTensor> values = [];
        internal TsTensor Add(TsTensor value) { values.Add(value); return value; }
        /// <summary>Releases temporary views and tensors in reverse construction order.</summary>
        public void Dispose() { for (int i = values.Count - 1; i >= 0; i--) values[i].Dispose(); }
    }
}
