using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using TensorSharp.Models;

namespace Darkspyre.Inference.Tensor.GemmaExperiment;

internal static class ImageProbe
{
    private const int MaxPatches = 2520;
    private const int PatchPixels = 768;

    internal static int Run(string provider, string model, string fixtures, string output)
    {
        if (provider is not ("cpu" or "webgpu" or "webgpu-plugin"))
            throw new ArgumentException("Image provider must be cpu, webgpu or webgpu-plugin.");
        Directory.CreateDirectory(output);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "images.json")));
        using var options = new SessionOptions
        {
            EnableProfiling = true,
            ProfileOutputPathPrefix = Path.Combine(output, "vision"),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        if (provider == "webgpu") options.AppendExecutionProvider("WebGPU", new Dictionary<string, string>());
        if (provider == "webgpu-plugin") Program.AppendWebGpuPlugin(options);
        var watch = Stopwatch.StartNew();
        using var vision = new InferenceSession(Path.Combine(model, "vision_encoder.onnx"), options);
        double visionLoadMs = watch.Elapsed.TotalMilliseconds;
        options.ProfileOutputPathPrefix = Path.Combine(output, "text");
        watch.Restart();
        using var text = new InferenceSession(Path.Combine(model, "model.onnx"), options);
        double textLoadMs = watch.Elapsed.TotalMilliseconds;
        foreach (var item in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            int tokens = item.GetProperty("softTokens").GetInt32();
            string caseOutput = Path.Combine(output, name);
            Directory.CreateDirectory(caseOutput);
            float[] patches = Read<float>(fixtures, name, "f32", item);
            long[] positions = Read<long>(fixtures, name, "i64", item);
            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<float>(patches, [1, MaxPatches, PatchPixels])),
                NamedOnnxValue.CreateFromTensor("pixel_position_ids", new DenseTensor<long>(positions, [1, MaxPatches, 2]))
            };
            // Pinned processor expands <|image|> inside BOI/EOI; tokenizer adds BOS/EOS.
            long[] ids = [2, 255999, .. Enumerable.Repeat(258880L, tokens), 258882, 1];
            var visionTimes = new List<double>();
            var textTimes = new List<double>();
            var vectors = new List<float[]>();
            for (int run = 0; run < 6; run++)
            {
                watch.Restart();
                using var encoded = vision.Run(inputs, ["image_features"]);
                var featureTensor = encoded.Single().AsTensor<float>();
                float[] features = featureTensor.ToArray();
                visionTimes.Add(watch.Elapsed.TotalMilliseconds);
                if (!featureTensor.Dimensions.SequenceEqual([tokens, 512]) || features.Any(v => !float.IsFinite(v)))
                    throw new InvalidDataException("Vision output shape or finite-value check failed.");
                File.WriteAllBytes(Path.Combine(caseOutput, $"features-{run}.f32"), MemoryMarshal.AsBytes(features.AsSpan()).ToArray());
                var textInputs = new[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, ids.Length])),
                    NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), [1, ids.Length])),
                    NamedOnnxValue.CreateFromTensor("image_features", new DenseTensor<float>(features, [tokens, 512])),
                    NamedOnnxValue.CreateFromTensor("video_features", new DenseTensor<float>([0, 512])),
                    NamedOnnxValue.CreateFromTensor("audio_features", new DenseTensor<float>([0, 512]))
                };
                watch.Restart();
                using var embedded = text.Run(textInputs, ["sentence_embedding"]);
                float[] vector = embedded.Single().AsTensor<float>().ToArray();
                textTimes.Add(watch.Elapsed.TotalMilliseconds);
                if (vector.Length != 768 || vector.Any(v => !float.IsFinite(v)))
                    throw new InvalidDataException("Image embedding did not return 768 finite values.");
                vectors.Add(vector);
                Console.WriteLine($"{name}/{run}: vision {visionTimes[^1]:F1} ms, embedding {textTimes[^1]:F1} ms");
            }
            File.WriteAllText(Path.Combine(caseOutput, "result.json"), JsonSerializer.Serialize(new
            {
                provider,
                name,
                tokens,
                inputIds = ids,
                visionLoadMs,
                textLoadMs,
                visionTimesMs = visionTimes,
                timesMs = textTimes,
                vectors,
                vector = vectors[^1],
                peakWorkingSetBytes = Program.PeakWorkingSet(),
                note = "Reference PIL patches isolate encoder execution; tokenizer and retrieval quality remain unqualified."
            }));
        }
        Console.WriteLine(vision.EndProfiling());
        Console.WriteLine(text.EndProfiling());
        return 0;
    }

    internal static int Preprocess(string fixtures, string output)
    {
        Directory.CreateDirectory(output);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtures, "images.json")));
        var processor = new Gemma4ImageProcessor(imageMean: [0, 0, 0], imageStd: [1, 1, 1], referenceSizing: true);
        var reports = new List<object>();
        bool passed = true;
        foreach (var item in manifest.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            var (pixels, width, height) = processor.ProcessImage(Path.Combine(fixtures, name + ".png"));
            int ph = height / 16, pw = width / 16, plane = width * height;
            float[] patches = new float[MaxPatches * PatchPixels];
            long[] positions = Enumerable.Repeat(-1L, MaxPatches * 2).ToArray();
            int index = 0;
            for (int row = 0; row < ph; row++)
                for (int col = 0; col < pw; col++)
                {
                    positions[(row * pw + col) * 2] = col;
                    positions[(row * pw + col) * 2 + 1] = row;
                    for (int dy = 0; dy < 16; dy++)
                        for (int dx = 0; dx < 16; dx++)
                            for (int c = 0; c < 3; c++)
                                patches[index++] = pixels[c * plane + (row * 16 + dy) * width + col * 16 + dx];
                }
            float[] reference = Read<float>(fixtures, name, "f32", item);
            long[] referencePositions = Read<long>(fixtures, name, "i64", item);
            double maxAbs = patches.Zip(reference, (a, b) => Math.Abs((double)a - b)).Max();
            bool shapeMatches = width == item.GetProperty("width").GetInt32() && height == item.GetProperty("height").GetInt32();
            bool positionsMatch = positions.SequenceEqual(referencePositions);
            // Pixel equality uses a float32 rounding allowance, not embedding similarity.
            bool parityPassed = shapeMatches && positionsMatch && maxAbs <= 1e-6;
            passed &= parityPassed;
            reports.Add(new
            {
                name,
                width,
                height,
                maxAbs,
                shapeMatches,
                positionsMatch,
                parityPassed,
                min = patches.Min(),
                max = patches.Max()
            });
            File.WriteAllBytes(Path.Combine(output, name + ".f32"), MemoryMarshal.AsBytes(patches.AsSpan()).ToArray());
        }
        string report = JsonSerializer.Serialize(reports);
        File.WriteAllText(Path.Combine(output, "preprocessing.json"), report);
        Console.WriteLine(report);
        return passed ? 0 : 1;
    }

    private static T[] Read<T>(string root, string name, string extension, JsonElement item) where T : unmanaged
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(root, name + "." + extension));
        string expected = item.GetProperty("sha256").GetProperty(extension).GetString()!;
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Image fixture hash mismatch: " + name + "." + extension);
        return MemoryMarshal.Cast<byte, T>(bytes).ToArray();
    }
}
