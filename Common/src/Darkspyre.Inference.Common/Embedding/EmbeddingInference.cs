namespace Darkspyre.Inference.Common.Embedding;

/// <summary>What an input is for. The model's runtime formats a query; a document is embedded as given.</summary>
public enum EmbeddingInputKind
{
    Document,
    Query
}

public sealed record EmbeddingRequest
{
    public required string ModelId { get; init; }

    public required IReadOnlyList<string> Inputs { get; init; }

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

public sealed record EmbeddingUsage(int InputTokens);

public sealed record EmbeddingResult
{
    /// <summary>One unit-length vector per input, in input order.</summary>
    public required IReadOnlyList<float[]> Vectors { get; init; }

    public required int Dimensions { get; init; }

    /// <summary>The <see cref="EmbeddingCapabilities.ModelFingerprint"/> of the model that produced the vectors.</summary>
    public required string ModelFingerprint { get; init; }

    /// <summary>The indices of inputs that were cut to fit the model's limit.</summary>
    public IReadOnlyList<int> TruncatedInputs { get; init; } = [];

    public required EmbeddingUsage Usage { get; init; }

    public required InferenceProvenance Provenance { get; init; }
}

public sealed record EmbeddingCapabilities
{
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
public sealed record EmbeddingOverLimitInput(int Index);

/// <summary>Inputs that exceed the model's limit in a request that does not permit truncation.</summary>
public sealed class EmbeddingInputTooLongException : Exception
{
    public EmbeddingInputTooLongException(IReadOnlyList<EmbeddingOverLimitInput> inputs, int maximumInputTokens)
        : base($"{inputs.Count} embedding input(s) exceed the model's limit of {maximumInputTokens} tokens.")
    {
        ArgumentNullException.ThrowIfNull(inputs);
        Inputs = inputs;
        MaximumInputTokens = maximumInputTokens;
    }

    public IReadOnlyList<EmbeddingOverLimitInput> Inputs { get; }

    public int MaximumInputTokens { get; }
}

public interface IEmbeddingInference
{
    InferenceImplementationDescriptor Descriptor { get; }

    Task<EmbeddingCapabilities> GetCapabilitiesAsync(string? modelId = null, CancellationToken cancellationToken = default);

    Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken = default);
}
