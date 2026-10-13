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
            Console.WriteLine("ONNX providers: " + string.Join(", ", OrtEnv.Instance().GetAvailableProviders()));
            if (args is ["providers"]) return 0;
            if (args.Length != 3 || args[0] is not ("cpu" or "coreml"))
                throw new ArgumentException("Usage: <cpu|coreml> <prepared-directory> <output-directory>, or providers");
            string root = Path.GetFullPath(args[1]);
            string output = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(output);
            using var options = new SessionOptions
            {
                EnableProfiling = true,
                ProfileOutputPathPrefix = Path.Combine(output, "ort"),
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            if (args[0] == "coreml")
                options.AppendExecutionProvider("CoreML", new Dictionary<string, string>
                {
                    ["ModelFormat"] = "MLProgram",
                    ["MLComputeUnits"] = "CPUAndGPU",
                    ["RequireStaticInputShapes"] = "0"
                });
            Stopwatch watch = Stopwatch.StartNew();
            using var session = new InferenceSession(Path.Combine(root, "model.onnx"), options);
            double loadMs = watch.Elapsed.TotalMilliseconds;
            Console.WriteLine($"Loaded {args[0]} in {loadMs:F1} ms");
            long[] ids = [2, 3369, 236787, 3253, 1534, 865, 2336, 236787, 1212, 563, 1009, 1];
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, ids.Length])),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), [1, ids.Length]))
            };
            foreach (string name in new[] { "image_features", "video_features", "audio_features" })
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>([0, 512])));
            var times = new List<double>();
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
                Console.WriteLine($"Run {i}: {elapsed:F1} ms; norm {Math.Sqrt(vector.Sum(value => (double)value * value)):F6}");
            }
            string profile = session.EndProfiling();
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                provider = args[0], inputIds = ids, loadMs, timesMs = times, vector, profile,
                peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
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
}
