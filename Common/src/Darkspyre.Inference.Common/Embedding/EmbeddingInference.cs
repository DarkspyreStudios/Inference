namespace Darkspyre.Inference.Common.Embedding;

/// <summary>What an input is for. The model's runtime formats a query; a document is embedded as given.</summary>
public enum EmbeddingInputKind
{
    /// <summary>Text to store and search; embedded as given.</summary>
    Document,
    /// <summary>Search text; the model's runtime formats it with the request instruction.</summary>
    Query
}

/// <summary>A batch of texts to embed with one model.</summary>
public sealed record EmbeddingRequest
{
    /// <summary>The model that embeds the inputs.</summary>
    public required string ModelId { get; init; }

    /// <summary>The texts to embed; the result holds one vector per input, in this order.</summary>
    public required IReadOnlyList<string> Inputs { get; init; }

    /// <summary>Whether every input is a document or a query.</summary>
    public EmbeddingInputKind Kind { get; init; } = EmbeddingInputKind.Document;

    /// <summary>The task a query states. Null uses the model's default; a document request must leave it null.</summary>
    public string? Instruction { get; init; }

    /// <summary>The vector size to return. Null returns the model's native size; another size must be supported.</summary>
    public int? Dimensions { get; init; }

    /// <summary>
    /// Whether an input longer than the model's limit is cut to fit. When false, such a request fails with
    /// <see cref="EmbeddingInputTooLongException"/> before any input is embedded.
    /// </summary>
    public bool Truncate { get; init; }
}

/// <summary>The tokens an embedding request consumed.</summary>
/// <param name="InputTokens">The input tokens the model read across every input.</param>
public sealed record EmbeddingUsage(int InputTokens);

/// <summary>The vectors an embedding request produced.</summary>
public sealed record EmbeddingResult
{
    /// <summary>One unit-length vector per input, in input order.</summary>
    public required IReadOnlyList<float[]> Vectors { get; init; }

    /// <summary>The length of every vector.</summary>
    public required int Dimensions { get; init; }

    /// <summary>The <see cref="EmbeddingCapabilities.ModelFingerprint"/> of the model that produced the vectors.</summary>
    public required string ModelFingerprint { get; init; }

    /// <summary>The indices of inputs that were cut to fit the model's limit.</summary>
    public IReadOnlyList<int> TruncatedInputs { get; init; } = [];

    /// <summary>The tokens the request consumed.</summary>
    public required EmbeddingUsage Usage { get; init; }

    /// <summary>Which implementation, model and runtime produced the vectors.</summary>
    public required InferenceProvenance Provenance { get; init; }
}

/// <summary>What an embedding model accepts and produces.</summary>
public sealed record EmbeddingCapabilities
{
    /// <summary>The vector size the model produces when a request names none.</summary>
    public required int NativeDimensions { get; init; }

    /// <summary>Every vector size a request may ask for, including the native size.</summary>
    public required IReadOnlyList<int> SupportedDimensions { get; init; }

    /// <summary>The most tokens one formatted input may hold, counting query formatting and terminal tokens.</summary>
    public required int MaximumInputTokens { get; init; }

    /// <summary>
    /// Identifies the vectors this model produces: its weights, tokenizer and query formatting. Vectors with
    /// different fingerprints are not comparable.
    /// </summary>
    public required string ModelFingerprint { get; init; }
}

/// <summary>One input over the model's limit, by its index in the request.</summary>
/// <param name="Index">The zero-based position of the input in the request.</param>
public sealed record EmbeddingOverLimitInput(int Index);

/// <summary>Inputs that exceed the model's limit in a request that does not permit truncation.</summary>
public sealed class EmbeddingInputTooLongException : Exception
{
    /// <summary>Creates the exception for the inputs over the limit and the limit they exceed.</summary>
    public EmbeddingInputTooLongException(IReadOnlyList<EmbeddingOverLimitInput> inputs, int maximumInputTokens)
        : base($"{inputs.Count} embedding input(s) exceed the model's limit of {maximumInputTokens} tokens.")
    {
        ArgumentNullException.ThrowIfNull(inputs);
        Inputs = inputs;
        MaximumInputTokens = maximumInputTokens;
    }

    /// <summary>The inputs over the model's limit.</summary>
    public IReadOnlyList<EmbeddingOverLimitInput> Inputs { get; }

    /// <summary>The most tokens one formatted input may hold.</summary>
    public int MaximumInputTokens { get; }
}

/// <summary>Turns text into unit-length vectors for similarity search.</summary>
public interface IEmbeddingInference
{
    /// <summary>The implementation that serves the embeddings.</summary>
    InferenceImplementationDescriptor Descriptor { get; }

    /// <summary>Returns the capabilities of the given model, or of the default model when <paramref name="modelId"/> is null.</summary>
    Task<EmbeddingCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default);

    /// <summary>Embeds every input of the request and returns one vector per input.</summary>
    Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default);
}
