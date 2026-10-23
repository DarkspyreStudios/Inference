using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Tensor.Common;
using Microsoft.ML.OnnxRuntime;

namespace Darkspyre.Inference.Tensor.Embedding.Gemma;

internal sealed record GemmaEmbeddingRun(IReadOnlyList<float[]> Vectors, IReadOnlyList<int> TruncatedInputs, int InputTokens);

internal interface IGemmaEmbeddingModel : IDisposable
{
    Task<GemmaEmbeddingRun> EmbedAsync(EmbeddingRequest request, TensorModelLifetime.ExecutionLease execution);
}

internal interface IGemmaEmbeddingLoader
{
    Task<IGemmaEmbeddingModel> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend, CancellationToken token);
}

internal sealed class GemmaEmbeddingLoader : IGemmaEmbeddingLoader
{
    public Task<IGemmaEmbeddingModel> LoadAsync(TensorModelPackLease lease, TensorBackendKind backend, CancellationToken token) =>
        Task.Run<IGemmaEmbeddingModel>(() => GemmaOnnxModel.Load(lease, backend, token), CancellationToken.None);
}

internal sealed class GemmaOnnxModel : IGemmaEmbeddingModel
{
    // Padded tokens per run: the fastest measured budget for short inputs on Apple WebGPU (2.4x), with no loss for long ones.
    private const int MaximumBatchTokens = 1024;
    private readonly GemmaTokenizer _tokenizer;
    private InferenceSession? _vision;
    private InferenceSession? _text;

    private GemmaOnnxModel(GemmaTokenizer tokenizer) => _tokenizer = tokenizer;

    internal static GemmaOnnxModel Load(TensorModelPackLease lease, TensorBackendKind backend, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var model = new GemmaOnnxModel(GemmaTokenizer.Load(lease.GetRequiredArtifact("tokenizer")));
        try
        {
            using SessionOptions options = GemmaOnnxBackend.CreateOptions(backend);
            token.ThrowIfCancellationRequested();
            model._vision = new(lease.GetRequiredArtifact("vision"), options);
            token.ThrowIfCancellationRequested();
            model._text = new(lease.GetRequiredArtifact("model"), options);
            token.ThrowIfCancellationRequested();
            return model;
        }
        catch { model.Dispose(); throw; }
    }

    public Task<GemmaEmbeddingRun> EmbedAsync(EmbeddingRequest request, TensorModelLifetime.ExecutionLease execution) =>
        Task.Run(() => EmbedCoreAsync(request, execution), CancellationToken.None);

    // A waiting query takes the engine before each input is prepared and before each ONNX run (execution.YieldAsync).
    private async Task<GemmaEmbeddingRun> EmbedCoreAsync(EmbeddingRequest request, TensorModelLifetime.ExecutionLease execution)
    {
        CancellationToken token = execution.CancellationToken;
        if (request.Inputs is null || request.Inputs.Count == 0 || !Enum.IsDefined(request.Kind))
            throw Invalid("An embedding request needs content and a defined input kind.");
        if (request.Dimensions is not null and not GemmaEmbeddingModels.Dimensions)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "EmbeddingGemma returns 768-dimensional vectors.");
        if (request.Instruction is not null)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "EmbeddingGemma uses its search-result query instruction.");
        var prepared = new PreparedInput[request.Inputs.Count];
        List<int> truncated = [];
        List<EmbeddingOverLimitInput> overLimit = [];
        // Admission covers the entire batch before either graph executes.
        for (int index = 0; index < request.Inputs.Count; index++)
        {
            await execution.YieldAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            prepared[index] = Prepare(request.Inputs[index], request.Kind);
            if (prepared[index].Ids.Length <= GemmaEmbeddingModels.MaximumTokens) continue;
            if (!request.Truncate) { overLimit.Add(new(index)); continue; }
            prepared[index] = prepared[index] with { Ids = [.. prepared[index].Ids[..(GemmaEmbeddingModels.MaximumTokens - 1)], 1] };
            truncated.Add(index);
        }
        if (overLimit.Count != 0) throw new EmbeddingInputTooLongException(overLimit.AsReadOnly(), GemmaEmbeddingModels.MaximumTokens);
        var vectors = new float[prepared.Length][];
        int inputTokens = 0;
        foreach (int[] batch in Batches(prepared))
        {
            token.ThrowIfCancellationRequested();
            int length = batch.Max(index => prepared[index].Ids.Length);
            var ids = new long[batch.Length * length];
            var mask = new long[batch.Length * length];
            var features = new List<float>();
            for (int row = 0; row < batch.Length; row++)
            {
                PreparedInput input = prepared[batch[row]];
                // Right padding keeps each input's positions; the mask removes the padding from attention and pooling.
                input.Ids.CopyTo(ids, row * length);
                Array.Fill(mask, 1L, row * length, input.Ids.Length);
                inputTokens += input.Ids.Length;
                if (input.Image is not { } image) continue;
                using var memory = HostMemory();
                using OrtValue pixels = OrtValue.CreateTensorValueFromMemory<float>(memory, image.Patches, [1, GemmaImageInput.MaxPatches, GemmaImageInput.PatchPixels]);
                using OrtValue positions = OrtValue.CreateTensorValueFromMemory<long>(memory, image.Positions, [1, GemmaImageInput.MaxPatches, 2]);
                await execution.YieldAsync().ConfigureAwait(false);
                float[] imageFeatures = Run(_vision!, ["pixel_values", "pixel_position_ids"], [pixels, positions], "image_features", token);
                if (imageFeatures.Length != (input.Ids.Length - 4) * 512)
                    throw new InvalidDataException("The vision encoder returned an unexpected feature shape.");
                // The text graph fills the image tokens of every row, in row order, from one feature list.
                features.AddRange(imageFeatures);
            }
            using var hostMemory = HostMemory();
            using OrtValue idValues = OrtValue.CreateTensorValueFromMemory<long>(hostMemory, ids, [batch.Length, length]);
            using OrtValue maskValues = OrtValue.CreateTensorValueFromMemory<long>(hostMemory, mask, [batch.Length, length]);
            using OrtValue images = OrtValue.CreateTensorValueFromMemory<float>(hostMemory, features.ToArray(), [features.Count / 512, 512]);
            using OrtValue absent = OrtValue.CreateTensorValueFromMemory<float>(hostMemory, Array.Empty<float>(), [0, 512]);
            await execution.YieldAsync().ConfigureAwait(false);
            float[] embeddings = Run(_text!, ["input_ids", "attention_mask", "image_features", "video_features", "audio_features"],
                [idValues, maskValues, images, absent, absent], "sentence_embedding", token);
            if (embeddings.Length != batch.Length * GemmaEmbeddingModels.Dimensions)
                throw new InvalidDataException("EmbeddingGemma returned an invalid embedding vector.");
            for (int row = 0; row < batch.Length; row++)
                vectors[batch[row]] = Normalize(embeddings.AsSpan(row * GemmaEmbeddingModels.Dimensions, GemmaEmbeddingModels.Dimensions));
        }
        return new(Array.AsReadOnly(vectors), truncated.AsReadOnly(), inputTokens);
    }

    // Inputs run shortest first, so each batch pads little. A batch holds at most MaximumBatchTokens padded tokens; an
    // input longer than that runs alone, so no run needs more memory than one input of the model's maximum length.
    private static IEnumerable<int[]> Batches(PreparedInput[] prepared)
    {
        var batch = new List<int>();
        foreach (int index in Enumerable.Range(0, prepared.Length).OrderBy(index => prepared[index].Ids.Length))
        {
            if ((batch.Count + 1) * prepared[index].Ids.Length > MaximumBatchTokens)
            {
                if (batch.Count != 0) yield return batch.ToArray();
                batch.Clear();
            }
            batch.Add(index);
        }
        if (batch.Count != 0) yield return batch.ToArray();
    }

    private static float[] Normalize(ReadOnlySpan<float> embedding)
    {
        double sum = 0;
        foreach (float value in embedding) sum += (double)value * value;
        double norm = Math.Sqrt(sum);
        if (!double.IsFinite(norm) || norm == 0) throw new InvalidDataException("EmbeddingGemma returned an invalid embedding vector.");
        var vector = new float[embedding.Length];
        for (int dimension = 0; dimension < vector.Length; dimension++) vector[dimension] = (float)(embedding[dimension] / norm);
        return vector;
    }

    private PreparedInput Prepare(EmbeddingInput input, EmbeddingInputKind kind)
    {
        if (input is TextEmbeddingInput text)
        {
            if (string.IsNullOrWhiteSpace(text.Text)) throw Invalid("An embedding text input cannot be empty.");
            string formatted = kind == EmbeddingInputKind.Query ? "task: search result | query: " + text.Text
                : "title: " + (text.Title ?? "none") + " | text: " + text.Text;
            return new(_tokenizer.Encode(formatted).Select(id => (long)id).ToArray(), null);
        }
        if (input is not MediaEmbeddingInput media || media.Media is null || media.Media.Data.IsEmpty)
            throw Invalid("An embedding input requires text or encoded image bytes.");
        if (media.Media.ContentType is not ("image/png" or "image/jpeg"))
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "EmbeddingGemma accepts PNG and JPEG image inputs.");
        GemmaImageInput image;
        try { image = GemmaImageInput.Prepare(media.Media.Data.ToArray()); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { throw new InferenceException(InferenceFailureCode.InvalidRequest, $"The supplied image could not be decoded: {error.Message}", error); }
        int softTokens = image.Width / 48 * (image.Height / 48);
        return new([2, 255999, .. Enumerable.Repeat(258880L, softTokens), 258882, 1], image);
    }

    // The external WebGPU provider requires Cpu/Default input memory; C#'s implicit CpuOutput tag aborts.
    private static OrtMemoryInfo HostMemory() => new("Cpu", OrtAllocatorType.DeviceAllocator, 0, OrtMemType.Default);

    private static float[] Run(InferenceSession session, string[] names, OrtValue[] values, string output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var options = new RunOptions();
        using CancellationTokenRegistration termination = token.Register(static state => ((RunOptions)state!).Terminate = true, options);
        try
        {
            using IDisposableReadOnlyCollection<OrtValue> results = session.Run(options, names, values, [output]);
            token.ThrowIfCancellationRequested();
            return results[0].GetTensorDataAsSpan<float>().ToArray();
        }
        catch (OnnxRuntimeException error) when (token.IsCancellationRequested)
        { throw new OperationCanceledException("The EmbeddingGemma request was cancelled.", error, token); }
    }

    public void Dispose()
    {
        _text?.Dispose(); _text = null;
        _vision?.Dispose(); _vision = null;
    }

    private static InferenceException Invalid(string message) => new(InferenceFailureCode.InvalidRequest, message);
    private sealed record PreparedInput(long[] Ids, GemmaImageInput? Image);
}
