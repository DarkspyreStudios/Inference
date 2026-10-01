using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Provider-neutral connection data resolved by the host. The host remains responsible for
/// retrieving the protected credential before returning this short-lived value.
/// </summary>
public sealed record LlmConnection
{
    // Resolved by the selected adapter for one operation. Never serialized or supplied by a host.
    internal Internal.ILlmContinuationCodec? ContinuationCodec { get; init; }
    internal string? ResolvedContinuationScope { get; init; }
    internal LlmAccountUsageObservations.Binding? AccountUsageObservationBinding { get; init; }

    // The connection's model list from LlmModelListCache, published by the service for one operation.
    // Never serialized or supplied by a host.
    internal IReadOnlyList<LlmModel>? Catalog { get; init; }

    /// <summary>
    /// Opaque host concurrency token covering the connection and credential state. Every credential
    /// write must atomically compare this value before replacing the credential.
    /// </summary>
    public required string Version { get; init; }

    public required LlmTransport Transport { get; init; }

    /// <summary>
    /// Base API URL, normally ending in <c>/v1</c>. Local CLI transports use an executable name
    /// or an absolute executable path instead.
    /// </summary>
    public required string Endpoint { get; init; }

    /// <summary>Serialized <see cref="LlmCredential"/>.</summary>
    public string? Credential { get; init; }

    public LlmVertexConfiguration? Vertex { get; init; }

    public LlmAzureConfiguration? Azure { get; init; }

    /// <summary>Chat Completions protocol choices; null selects endpoint-aware defaults.</summary>
    public LlmChatCompletionsConfiguration? ChatCompletions { get; init; }

    /// <summary>Non-standard headers such as OpenRouter attribution headers.</summary>
    public IReadOnlyDictionary<string, string> AdditionalHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Provider-specific JSON properties added without replacing standard request fields.</summary>
    public IReadOnlyDictionary<string, JsonElement> ProviderOptions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public LlmCapabilityOverrides? CapabilityOverrides { get; init; }

    /// <summary>Optional capability corrections for individual model ids.</summary>
    public IReadOnlyDictionary<string, LlmCapabilityOverrides> ModelCapabilityOverrides { get; init; } =
        new Dictionary<string, LlmCapabilityOverrides>(StringComparer.Ordinal);
}
