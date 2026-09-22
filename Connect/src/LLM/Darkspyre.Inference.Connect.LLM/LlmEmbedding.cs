using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM;

public sealed record LlmEmbeddingRequest
{
    public required string ModelId { get; init; }

    public required IReadOnlyList<string> Inputs { get; init; }

    public IReadOnlyDictionary<string, JsonElement> ProviderOptions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}

public sealed record LlmEmbeddingUsage(int? InputTokens, int? TotalTokens);

public sealed record LlmEmbeddingResult
{
    public required IReadOnlyList<float[]> Vectors { get; init; }

    public LlmEmbeddingUsage? Usage { get; init; }

    public required LlmProviderDiagnostics Diagnostics { get; init; }
}
