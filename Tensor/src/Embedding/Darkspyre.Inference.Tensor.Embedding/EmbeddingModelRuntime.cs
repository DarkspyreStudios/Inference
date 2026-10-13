using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Embedding;
using TensorSharp.Models.Embeddings;

namespace Darkspyre.Inference.Tensor.Embedding;

/// <summary>The vectors of one request, cut to the requested size, with what the model reported.</summary>
internal sealed record EmbeddingRun(IReadOnlyList<float[]> Vectors, int Dimensions, IReadOnlyList<int> TruncatedInputs, int InputTokens);

/// <summary>
/// Runs one request on a loaded model. It formats queries, counts every input against the model's limit before any
/// input is embedded, and cuts and renormalises vectors to the requested size.
/// </summary>
internal static class EmbeddingModelRuntime
{
    public static async Task<EmbeddingRun> EmbedAsync(IEmbeddingModel model, TensorEmbeddingArchitecture architecture,
        EmbeddingRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(architecture);
        ArgumentNullException.ThrowIfNull(request);
        int dimensions = Validate(model, architecture, request);
        var tokens = new int[request.Inputs.Count][];
        List<int> truncated = [];
        List<EmbeddingOverLimitInput> overLimit = [];
        for (int index = 0; index < request.Inputs.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            string content = ((TextEmbeddingInput)request.Inputs[index]).Text;
            string text = request.Kind == EmbeddingInputKind.Query
                ? architecture.FormatQuery(content, request.Instruction) : content;
            if (Tokenize(model, text) is { } whole) { tokens[index] = whole; continue; }
            // The tokenizer refuses an input over the limit without reporting its length.
            tokens[index] = TokenizeCut(model, text);
            if (request.Truncate) truncated.Add(index);
            else overLimit.Add(new(index));
        }
        if (overLimit.Count > 0) throw new EmbeddingInputTooLongException(overLimit.AsReadOnly(), model.MaxTokens);

        EmbeddingBatchResult result = await model.EmbedTokensAsync(tokens, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result.Embeddings.Length != tokens.Length || result.Embeddings.Any(vector => vector.Length != model.Dimensions))
            throw new InferenceException(InferenceFailureCode.ExecutionFailed, "The embedding model returned vectors of the wrong number or size.");
        return new(Array.AsReadOnly(result.Embeddings.Select(vector => Reduce(vector, dimensions)).ToArray()), dimensions,
            truncated.AsReadOnly(), result.PromptTokens);
    }

    /// <summary>The vector's first <paramref name="dimensions"/> values scaled back to unit length.</summary>
    internal static float[] Reduce(float[] vector, int dimensions)
    {
        float[] reduced = vector[..dimensions];
        double norm = Math.Sqrt(reduced.Sum(value => (double)value * value));
        if (norm == 0 || !double.IsFinite(norm))
            throw new InferenceException(InferenceFailureCode.ExecutionFailed, "The embedding model returned a vector with no direction.");
        for (int index = 0; index < reduced.Length; index++) reduced[index] = (float)(reduced[index] / norm);
        return reduced;
    }

    private static int Validate(IEmbeddingModel model, TensorEmbeddingArchitecture architecture, EmbeddingRequest request)
    {
        static InferenceException Invalid(string message) => new(InferenceFailureCode.InvalidRequest, message);
        if (request.Inputs is null || request.Inputs.Count == 0 || request.Inputs.Any(input => input is null))
            throw Invalid("An embedding request needs at least one input, and no input may be empty.");
        if (request.Inputs.Any(input => input is not TextEmbeddingInput))
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature, "This embedding model accepts text only.");
        if (request.Inputs.Cast<TextEmbeddingInput>().Any(input => string.IsNullOrWhiteSpace(input.Text)))
            throw Invalid("An embedding request needs at least one input, and no input may be empty.");
        if (!Enum.IsDefined(request.Kind)) throw Invalid("The embedding input kind is not defined.");
        if (request.Instruction is not null && (request.Kind != EmbeddingInputKind.Query || !architecture.TakesInstruction))
            throw Invalid("Only a query to a model that takes an instruction may state one.");
        int dimensions = request.Dimensions ?? model.Dimensions;
        if (!architecture.SupportedDimensions.Contains(dimensions) || dimensions > model.Dimensions)
            throw new InferenceException(InferenceFailureCode.UnsupportedFeature,
                $"This model returns vectors of {string.Join(", ", architecture.SupportedDimensions)} dimensions, not {dimensions}.");
        return dimensions;
    }

    private static int[]? Tokenize(IEmbeddingModel model, string text)
    {
        try { return model.Tokenize(text, truncate: false); }
        catch (ArgumentException) { return null; }
    }

    private static int[] TokenizeCut(IEmbeddingModel model, string text)
    {
        try { return model.Tokenize(text, truncate: true); }
        catch (ArgumentException error)
        { throw new InferenceException(InferenceFailureCode.InvalidRequest, "An embedding input produces no tokens.", error); }
    }
}
