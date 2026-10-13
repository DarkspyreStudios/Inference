using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Tensor.Common;
using Microsoft.ML.OnnxRuntime;

namespace Darkspyre.Inference.Tensor.Embedding.Gemma;

internal sealed record GemmaEmbeddingRun(IReadOnlyList<float[]> Vectors, IReadOnlyList<int> TruncatedInputs, int InputTokens);

internal interface IGemmaEmbeddingModel : IDisposable
{
    Task<GemmaEmbeddingRun> EmbedAsync(EmbeddingRequest request, CancellationToken token);
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

    public Task<GemmaEmbeddingRun> EmbedAsync(EmbeddingRequest request, CancellationToken token) =>
        Task.Run(() => Embed(request, token), CancellationToken.None);

    private GemmaEmbeddingRun Embed(EmbeddingRequest request, CancellationToken token)
    {
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
        for (int index = 0; index < prepared.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            PreparedInput input = prepared[index];
            float[] features = [];
            if (input.Image is { } image)
            {
                using var memory = HostMemory();
                using OrtValue pixels = OrtValue.CreateTensorValueFromMemory<float>(memory, image.Patches, [1, GemmaImageInput.MaxPatches, GemmaImageInput.PatchPixels]);
                using OrtValue positions = OrtValue.CreateTensorValueFromMemory<long>(memory, image.Positions, [1, GemmaImageInput.MaxPatches, 2]);
                features = Run(_vision!, ["pixel_values", "pixel_position_ids"], [pixels, positions], "image_features", token);
                if (features.Length != (input.Ids.Length - 4) * 512)
                    throw new InvalidDataException("The vision encoder returned an unexpected feature shape.");
            }
            using var hostMemory = HostMemory();
            using OrtValue ids = OrtValue.CreateTensorValueFromMemory<long>(hostMemory, input.Ids, [1, input.Ids.Length]);
            using OrtValue mask = OrtValue.CreateTensorValueFromMemory<long>(hostMemory, Enumerable.Repeat(1L, input.Ids.Length).ToArray(), [1, input.Ids.Length]);
            using OrtValue images = OrtValue.CreateTensorValueFromMemory<float>(hostMemory, features, [features.Length / 512, 512]);
            using OrtValue absent = OrtValue.CreateTensorValueFromMemory<float>(hostMemory, Array.Empty<float>(), [0, 512]);
            float[] vector = Run(_text!, ["input_ids", "attention_mask", "image_features", "video_features", "audio_features"],
                [ids, mask, images, absent, absent], "sentence_embedding", token);
            double norm = Math.Sqrt(vector.Sum(value => (double)value * value));
            if (vector.Length != GemmaEmbeddingModels.Dimensions || !double.IsFinite(norm) || norm == 0)
                throw new InvalidDataException("EmbeddingGemma returned an invalid embedding vector.");
            for (int dimension = 0; dimension < vector.Length; dimension++) vector[dimension] = (float)(vector[dimension] / norm);
            vectors[index] = vector;
            inputTokens += input.Ids.Length;
        }
        return new(Array.AsReadOnly(vectors), truncated.AsReadOnly(), inputTokens);
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
        { throw new InferenceException(InferenceFailureCode.InvalidRequest, "The supplied image could not be decoded.", error); }
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
