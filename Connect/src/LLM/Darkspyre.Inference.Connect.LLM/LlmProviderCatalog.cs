namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Provides canonical provider metadata without resolving connections, credentials, or runtime
/// clients.
/// </summary>
public sealed class LlmProviderCatalog
{
    public LlmProviderCatalog(IEnumerable<LlmProviderDefinition> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        LlmProviderDefinition[] definitions = [.. providers];
        IGrouping<string, LlmProviderDefinition>? duplicateId = definitions
            .GroupBy(provider => provider.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicateId is not null)
        {
            throw new InvalidOperationException(
                $"Multiple LLM provider definitions are registered with id '{duplicateId.Key}'.");
        }

        IGrouping<LlmTransport, LlmProviderDefinition>? duplicateTransport = definitions
            .GroupBy(provider => provider.Transport)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicateTransport is not null)
        {
            throw new InvalidOperationException(
                $"Multiple LLM provider definitions are registered for transport {duplicateTransport.Key}.");
        }

        SupportedProviders =
        [
            .. definitions
                .OrderBy(provider => provider.DisplayOrder)
                .ThenBy(provider => provider.DisplayName, StringComparer.Ordinal)
        ];
    }

    /// <summary>Canonical provider metadata and guided setup supplied by registered adapters.</summary>
    public IReadOnlyList<LlmProviderDefinition> SupportedProviders { get; }
}
