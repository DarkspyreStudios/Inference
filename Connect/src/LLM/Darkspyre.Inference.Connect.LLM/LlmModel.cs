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
}
