namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Provides canonical provider metadata without resolving connections, credentials, or runtime
/// clients.
/// </summary>
public sealed class LlmProviderCatalog
{
    /// <summary>Creates the catalog, ordered by display order and then display name. Throws <see cref="InvalidOperationException"/> when two definitions share an id or a transport.</summary>
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

    /// <summary>The setup definitions of the supported providers, in catalog order.</summary>
    public IReadOnlyList<Common.Setup.InferenceSetupDefinition> SetupDefinitions =>
        SupportedProviders.Select(provider => provider.SetupDefinition).ToArray();
}
