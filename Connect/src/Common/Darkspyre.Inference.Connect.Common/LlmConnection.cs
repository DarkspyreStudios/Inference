using System.Text.Json;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>
/// Provider-neutral connection data resolved by the host. The host remains responsible for
/// retrieving the protected credential before returning this short-lived value.
/// </summary>
public sealed record LlmConnection
{
    // Resolved by the selected adapter for one operation. Never serialized or supplied by a host.
    // The Gemini API, OpenAI-compatible Chat and Google Vertex AI use this; they are out of the build
    // (Providers/GeminiApiLlmClient*.cs and ChatCompletionsLlmClientBase.cs, under Compile Remove;
    // Connect/src/LLM/Darkspyre.Inference.Connect.LLM.Vertex, outside the solution). It moves into their own libraries
    // when they return to the build.
    internal Internal.ILlmContinuationCodec? ContinuationCodec { get; init; }
    internal string? ResolvedContinuationScope { get; init; }
    internal LlmAccountUsageObservations.Binding? AccountUsageObservationBinding { get; init; }
    internal Internal.GeminiCacheBindings? CacheBindings { get; init; }
    internal Internal.GeminiCacheBindings.Admission? CacheAdmission { get; init; }
    internal Internal.ILlmContextCacheOperation? CacheOperation { get; init; }
    // Equality evidence for this admitted credential source, never a stable principal identity.
    internal string? CacheCredentialEvidence { get; init; }

    // The connection's model list from LlmModelListCache, published by the service for one operation.
    // Never serialized or supplied by a host.
    internal IReadOnlyList<LlmModel>? Catalog { get; init; }

    /// <summary>
    /// Opaque host concurrency token covering the connection and credential state. Every credential
    /// write must atomically compare this value before replacing the credential.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>The transport that selects the provider protocol.</summary>
    public required LlmTransport Transport { get; init; }

    /// <summary>
    /// Base API URL, normally ending in <c>/v1</c>. Local CLI transports use an executable name
    /// or an absolute executable path instead.
    /// </summary>
    public required string Endpoint { get; init; }

    /// <summary>The explicit native llama.cpp tokenization URL for the Responses transport. Null leaves plain-text tokenization unavailable.</summary>
    public Uri? LlamaCppTokenizationEndpoint { get; init; }

    /// <summary>Serialized <see cref="LlmCredential"/>.</summary>
    public string? Credential { get; init; }

    // Saved with each connection. It stays in Connect.Common because LlmConnection, which hosts store, holds
    // it; moving it would change saved connections.
    /// <summary>Google Vertex AI settings, when the connection uses Vertex.</summary>
    public LlmVertexConfiguration? Vertex { get; init; }

    // Saved with each connection. It stays in Connect.Common because LlmConnection, which hosts store, holds
    // it; moving it would change saved connections.
    /// <summary>Azure settings, when the connection uses Azure.</summary>
    public LlmAzureConfiguration? Azure { get; init; }

    // Saved with each connection. It stays in Connect.Common because LlmConnection, which hosts store, holds
    // it; moving it would change saved connections.
    /// <summary>Agent Client Protocol settings, when the connection uses an ACP agent.</summary>
    public LlmAcpConfiguration? Acp { get; init; }

    // Saved with each connection. It stays in Connect.Common because LlmConnection, which hosts store, holds
    // it; moving it would change saved connections.
    /// <summary>GitHub Copilot settings, when the connection uses Copilot.</summary>
    public LlmCopilotConfiguration? Copilot { get; init; }

    /// <summary>Transient explicit launch values; non-null values cannot be serialized as configuration.</summary>
    public LlmAgentRuntimeLaunchInputs? RuntimeLaunchInputs { get; init; }

    // Saved with each connection. It stays in Connect.Common because LlmConnection, which hosts store, holds
    // it; moving it would change saved connections.
    /// <summary>Chat Completions protocol choices; null selects endpoint-aware defaults.</summary>
    public LlmChatCompletionsConfiguration? ChatCompletions { get; init; }

    // Saved with each connection. It stays in Connect.Common because LlmConnection, which hosts store, holds
    // it; moving it would change saved connections.
    /// <summary>xAI settings, when the connection uses xAI.</summary>
    public XaiConnectionOptions? Xai { get; init; }

    /// <summary>Non-standard headers such as OpenRouter attribution headers.</summary>
    public IReadOnlyDictionary<string, string> AdditionalHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Provider-specific JSON properties added without replacing standard request fields.</summary>
    public IReadOnlyDictionary<string, JsonElement> ProviderOptions { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>Capability corrections applied to every model on the connection.</summary>
    public LlmCapabilityOverrides? CapabilityOverrides { get; init; }

    /// <summary>Optional capability corrections for individual model ids.</summary>
    public IReadOnlyDictionary<string, LlmCapabilityOverrides> ModelCapabilityOverrides { get; init; } =
        new Dictionary<string, LlmCapabilityOverrides>(StringComparer.Ordinal);

    /// <summary>
    /// The context limits, in tokens by model id, that too-long refusals taught on this connection and
    /// <see cref="ILlmLearnedLimitStore"/> saved. A limit learned in this process takes their place.
    /// </summary>
    public IReadOnlyDictionary<string, long> LearnedContextLimits { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);
}
