using System.Text.Json;
using Darkspyre.Inference.Common.Embedding;

namespace Darkspyre.Inference.Connect.LLM;

public sealed record LlmEmbeddingRequest
{
    public required string ModelId { get; init; }

    public required IReadOnlyList<string> Inputs { get; init; }

    /// <summary>Requested vector size; null preserves the model default.</summary>
    public int? Dimensions { get; init; }

    /// <summary>
    /// Whether the inputs are queries or documents. A SelfHost engine formats queries for its model; a remote
    /// provider receives the inputs as given.
    /// </summary>
    public EmbeddingInputKind Kind { get; init; } = EmbeddingInputKind.Document;

    /// <summary>The task a query states, applied by a SelfHost engine only. Null uses the model's default.</summary>
    public string? Instruction { get; init; }

    /// <summary>
    /// Whether a SelfHost engine cuts an over-limit input to fit. When false the request fails with
    /// <see cref="EmbeddingInputTooLongException"/> before any input is embedded.
    /// </summary>
    public bool Truncate { get; init; }

    public IReadOnlyDictionary<string, JsonElement> ProviderOptions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public sealed record LlmEmbeddingUsage(int? InputTokens, int? TotalTokens);

public sealed record LlmEmbeddingResult
{
    public required IReadOnlyList<float[]> Vectors { get; init; }

    public LlmEmbeddingUsage? Usage { get; init; }

    /// <summary>The model that produced the vectors, when the provider reports it.</summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Identifies the weights, architecture and query format behind the vectors, when a SelfHost engine reports it.
    /// Vectors with different fingerprints are not comparable.
    /// </summary>
    public string? ModelFingerprint { get; init; }

    /// <summary>The indices of inputs a SelfHost engine cut to fit the model's limit.</summary>
    public IReadOnlyList<int> TruncatedInputs { get; init; } = [];

    public required LlmProviderDiagnostics Diagnostics { get; init; }
}
