using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Darkspyre.Inference.Tensor.GemmaExperiment;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["providers"])
            {
                Console.WriteLine("ONNX providers: " + string.Join(", ", OrtEnv.Instance().GetAvailableProviders()));
                return 0;
            }
            if (args.Length is not (3 or 4) || args[0] is not ("cpu" or "coreml" or "coreml-static" or "webgpu" or "metal" or "metal-resident"))
                throw new ArgumentException("Usage: <cpu|coreml|coreml-static|webgpu|metal|metal-resident> <prepared-directory> <output-directory> [tokens: 2..512], or providers");
            string root = Path.GetFullPath(args[1]);
            string output = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(output);
            int length = args.Length == 4 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 12;
            // Up to 512 tokens, the full and sliding attention masks both admit every token.
            // Longer inputs require the sliding mask, which this text execution probe omits.
            if (length is < 2 or > 512) throw new ArgumentException("The probe accepts 2..512 tokens.");
            long[] interior = [3369, 236787, 3253, 1534, 865, 2336, 236787, 1212, 563, 1009];
            long[] ids = [2, .. Enumerable.Range(0, length - 2).Select(i => interior[i % interior.Length]), 1];
            if (args[0] is "metal" or "metal-resident") return MetalText.Run(root, output, ids, args[0] == "metal-resident");
            Console.WriteLine("ONNX providers: " + string.Join(", ", OrtEnv.Instance().GetAvailableProviders()));
            using var options = new SessionOptions
            {
                ProfileOutputPathPrefix = Path.Combine(output, "ort"),
                EnableProfiling = true,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            if (args[0] == "coreml-static")
            {
                options.AddFreeDimensionOverrideByName("batch_size", 1);
                options.AddFreeDimensionOverrideByName("sequence_length", length);
                foreach (string name in new[] { "num_image_tokens", "num_video_tokens", "num_audio_tokens" })
                    options.AddFreeDimensionOverrideByName(name, 0);
            }
            if (args[0] is "coreml" or "coreml-static")
                options.AppendExecutionProvider("CoreML", new Dictionary<string, string>
                {
                    ["ModelFormat"] = "MLProgram",
                    ["MLComputeUnits"] = "CPUAndGPU",
                    ["RequireStaticInputShapes"] = args[0] == "coreml-static" ? "1" : "0"
                });
            if (args[0] == "webgpu")
                options.AppendExecutionProvider("WebGPU", new Dictionary<string, string>());
            Stopwatch watch = Stopwatch.StartNew();
            using var session = new InferenceSession(Path.Combine(root, "model.onnx"), options);
            double loadMs = watch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Loaded {args[0]} in {loadMs:F1} ms");
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, ids.Length])),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), [1, ids.Length]))
            };
            foreach (string name in new[] { "image_features", "video_features", "audio_features" })
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>([0, 512])));
            var times = new List<double>();
            var vectors = new List<float[]>();
            float[] vector = [];
            for (int i = 0; i < 6; i++)
            {
                watch.Restart();
                using var result = session.Run(inputs, ["sentence_embedding"]);
                vector = result.Single().AsTensor<float>().ToArray();
                double elapsed = watch.Elapsed.TotalMilliseconds;
                if (vector.Length != 768 || vector.Any(value => !float.IsFinite(value)))
                    throw new InvalidDataException("The model did not return 768 finite values.");
                times.Add(elapsed);
                vectors.Add(vector);
                Console.WriteLine($"Run {i}: {elapsed:F1} ms; norm {Math.Sqrt(vector.Sum(value => (double)value * value)):F6}");
            }
            string profile = session.EndProfiling();
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                provider = args[0],
                inputIds = ids,
                loadMs,
                timesMs = times,
                vector,
                vectors,
                profile,
                peakWorkingSetBytes = PeakWorkingSet(),
                note = "Frozen token IDs isolate model execution; tokenizer and retrieval quality are not qualified."
            }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    internal static long? PeakWorkingSet()
    {
        using Process process = Process.GetCurrentProcess();
        long peak = process.PeakWorkingSet64;
        return peak > 0 ? peak : null;
    }
}
