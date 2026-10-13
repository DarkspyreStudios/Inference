using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.OnnxRuntime.EP.WebGpu;

namespace Darkspyre.Inference.Tensor.GemmaExperiment;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["images" or "images-shared", var imageProvider, var model, var fixtures, var imageResult])
                return ImageProbe.Run(imageProvider, Path.GetFullPath(model), Path.GetFullPath(fixtures), Path.GetFullPath(imageResult), args[0] == "images-shared");
            if (args is ["preprocess", var imageFixtures, var imageOutput])
                return ImageProbe.Preprocess(Path.GetFullPath(imageFixtures), Path.GetFullPath(imageOutput));
            if (args is ["providers"])
            {
                Console.WriteLine("ONNX providers: " + string.Join(", ", OrtEnv.Instance().GetAvailableProviders()));
                return 0;
            }
            if (args.Length is not (3 or 4) || args[0] is not ("cpu" or "webgpu-plugin" or "coreml" or "coreml-static" or "webgpu" or "metal" or "metal-resident" or "metal-fused" or "metal-grouped" or "metal-rows" or "ggml-cpu" or "ggml-cpu-fused"))
                throw new ArgumentException("Usage: <cpu|webgpu-plugin|coreml|coreml-static|webgpu|metal|metal-resident|metal-fused|metal-grouped|metal-rows|ggml-cpu|ggml-cpu-fused> <prepared-directory> <output-directory> [tokens: 2..512], or providers");
            string root = Path.GetFullPath(args[1]);
            string output = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(output);
            int length = args.Length == 4 ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 12;
            // Up to 512 tokens, the full and sliding attention masks both admit every token.
            // Longer inputs require the sliding mask, which this text execution probe omits.
            if (length is < 2 or > 512) throw new ArgumentException("The probe accepts 2..512 tokens.");
            long[] interior = [3369, 236787, 3253, 1534, 865, 2336, 236787, 1212, 563, 1009];
            long[] ids = [2, .. Enumerable.Range(0, length - 2).Select(i => interior[i % interior.Length]), 1];
            if (args[0] is "metal" or "metal-resident" or "metal-fused" or "metal-grouped" or "metal-rows" or "ggml-cpu" or "ggml-cpu-fused")
            {
                MetalVariant variant = args[0] switch
                {
                    "metal-resident" => MetalVariant.Resident,
                    "metal-fused" or "ggml-cpu-fused" => MetalVariant.Fused,
                    "metal-grouped" => MetalVariant.Grouped,
                    "metal-rows" => MetalVariant.Rows,
                    _ => MetalVariant.Baseline
                };
                return MetalText.Run(root, output, ids, variant, args[0].StartsWith("ggml-cpu", StringComparison.Ordinal)
                    ? TensorSharp.GGML.GgmlBackendType.Cpu : TensorSharp.GGML.GgmlBackendType.Metal);
            }
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
            if (args[0] == "webgpu-plugin") AppendWebGpuPlugin(options);
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
                vector = RunFloat(session, inputs, "sentence_embedding").ToArray();
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

    internal static DenseTensor<float> RunFloat(InferenceSession session, IReadOnlyCollection<NamedOnnxValue> inputs, string outputName)
    {
        if (Environment.GetEnvironmentVariable("GEMMA_DEFAULT_HOST_MEMORY") != "1")
        {
            using var result = session.Run(inputs, [outputName]);
            var tensor = result.Single().AsTensor<float>();
            return new DenseTensor<float>(tensor.ToArray(), tensor.Dimensions);
        }
        // Explicit CPU/default memory is valid host input. The 0.4 plugin adapter
        // rejects the -1 memory tag used by C#'s default CPU-output memory info.
        using var memory = new OrtMemoryInfo("Cpu", OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default);
        var values = new List<OrtValue>();
        try
        {
            foreach (var input in inputs)
                values.Add(input.Value switch
                {
                    Microsoft.ML.OnnxRuntime.Tensors.Tensor<float> t => OrtValue.CreateTensorValueFromMemory<float>(memory, t.ToArray(), t.Dimensions.ToArray().Select(d => (long)d).ToArray()),
                    Microsoft.ML.OnnxRuntime.Tensors.Tensor<long> t => OrtValue.CreateTensorValueFromMemory<long>(memory, t.ToArray(), t.Dimensions.ToArray().Select(d => (long)d).ToArray()),
                    _ => throw new InvalidDataException("The probe accepts FP32 and int64 inputs.")
                });
            using var run = new RunOptions();
            using var result = session.Run(run, inputs.Select(i => i.Name).ToArray(), values, [outputName]);
            var shape = result[0].GetTensorTypeAndShape().Shape.Select(d => checked((int)d)).ToArray();
            return new DenseTensor<float>(result[0].GetTensorDataAsSpan<float>().ToArray(), shape);
        }
        finally
        {
            foreach (var value in values) value.Dispose();
        }
    }

    internal static void AppendWebGpuPlugin(SessionOptions options)
    {
        var environment = OrtEnv.Instance();
        environment.RegisterExecutionProviderLibrary("gemma_webgpu", WebGpuEp.GetLibraryPath());
        string? vendor = Environment.GetEnvironmentVariable("GEMMA_GPU_VENDOR");
        var devices = environment.GetEpDevices().Where(d => d.EpName == WebGpuEp.GetEpName()
            && d.EpMetadata.Entries.ContainsKey("library_path")).ToArray();
        var device = devices.FirstOrDefault(d => vendor is null
            || d.HardwareDevice.VendorId.ToString(System.Globalization.CultureInfo.InvariantCulture) == vendor)
            ?? throw new InvalidOperationException("The packaged WebGPU provider found no matching device.");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            device.EpName,
            device.EpVendor,
            device.EpMetadata,
            device.EpOptions,
            type = device.HardwareDevice.Type.ToString(),
            device.HardwareDevice.Vendor,
            device.HardwareDevice.VendorId,
            device.HardwareDevice.DeviceId,
            device.HardwareDevice.Metadata
        }));
        options.AppendExecutionProvider(environment, [device], new Dictionary<string, string>());
    }

    internal static long? PeakWorkingSet()
    {
        using Process process = Process.GetCurrentProcess();
        long peak = process.PeakWorkingSet64;
        return peak > 0 ? peak : null;
    }
}
