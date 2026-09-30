using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

public sealed record LlmModel(
    string Id,
    string DisplayName,
    string? OwnedBy = null,
    LlmProviderCapabilities? Capabilities = null)
{
    /// <summary>Verified discovery limit; null means unknown, not unlimited.</summary>
    public long? MaxContextTokens { get; init; }

    public long? MaxOutputTokens { get; init; }

    public LlmModelAvailability Availability { get; init; }

    public DateTimeOffset? AvailabilityCheckedAt { get; init; }

    public string? AvailabilityReason { get; init; }

    /// <summary>The provider's own catalog entry, kept for the adapter that listed it. Not part of the host contract.</summary>
    internal JsonElement? CatalogEntry { get; init; }
}
