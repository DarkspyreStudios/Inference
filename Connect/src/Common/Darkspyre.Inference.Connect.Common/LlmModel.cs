using System.Text.Json;
using Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>A model that a provider connection lists.</summary>
/// <param name="Id">The provider's model id, used in requests.</param>
/// <param name="DisplayName">The name shown to users.</param>
/// <param name="OwnedBy">The organization the provider reports as the model's owner, or null when it reports none.</param>
/// <param name="Capabilities">The model's provider capabilities, or null when they are unknown.</param>
public sealed record LlmModel(
    string Id,
    string DisplayName,
    string? OwnedBy = null,
    LlmProviderCapabilities? Capabilities = null)
{
    /// <summary>Verified discovery limit; null means unknown, not unlimited.</summary>
    public long? MaxContextTokens { get; init; }

    /// <summary>Verified output token limit; null means unknown, not unlimited.</summary>
    public long? MaxOutputTokens { get; init; }

    /// <summary>Whether this connection can use the model, as distinct from whether the model supports a feature.</summary>
    public LlmModelAvailability Availability { get; init; }

    /// <summary>When the availability evidence was gathered, or null when it was never checked.</summary>
    public DateTimeOffset? AvailabilityCheckedAt { get; init; }

    /// <summary>A short explanation of the availability state, or null when the provider gives none.</summary>
    public string? AvailabilityReason { get; init; }

    /// <summary>The provider's own catalog entry, kept for the adapter that listed it. Not part of the host contract.</summary>
    internal JsonElement? CatalogEntry { get; init; }
}
