using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Connect-owned metadata for closed connection, request and captured-context contracts.</summary>
public static class ConnectLlmJson
{
    private static readonly JsonSerializerOptions OwnedOptions = CreateOptions();

    private static JsonTypeInfo<T> Metadata<T>() => OwnedOptions.GetTypeInfo<T>();

    private static JsonSerializerOptions CreateOptions()
    {
        // Retain the generated context converter and reuse Common's live-authority refusal policy.
        var options = new JsonSerializerOptions(ConnectLlmJsonContext.Default.Options)
        { TypeInfoResolver = ConnectLlmJsonContext.Default };
        foreach (Type type in new[] { typeof(IInferenceResourceAccess), typeof(IInferenceResourceReadHandle), typeof(IInferenceResourceWriteHandle) })
            options.Converters.Add(InferenceJson.InferenceResourceReference.Options.Converters.Single(converter => converter.CanConvert(type)));
        options.MakeReadOnly();
        return options;
    }

    public static JsonTypeInfo<LlmCredential> Credential => LlmCredentialJsonContext.Default.LlmCredential;
    public static JsonTypeInfo<LlmAccountUsageSnapshot> AccountUsageSnapshot => Metadata<LlmAccountUsageSnapshot>();
    public static JsonTypeInfo<LlmConnection> Connection => Metadata<LlmConnection>();
    public static JsonTypeInfo<LlmSetupConfiguration> SetupConfiguration => Metadata<LlmSetupConfiguration>();
    public static JsonTypeInfo<LlmChatRequest> ChatRequest => Metadata<LlmChatRequest>();
    public static JsonTypeInfo<LlmChatResult> ChatResult => Metadata<LlmChatResult>();
    public static JsonTypeInfo<LlmChatMessage> ChatMessage => Metadata<LlmChatMessage>();
    public static JsonTypeInfo<LlmTokenization> Tokenization => Metadata<LlmTokenization>();
    public static JsonTypeInfo<LlmToken> Token => Metadata<LlmToken>();
    public static JsonTypeInfo<LlmEmbeddingRequest> LlmEmbeddingRequest => Metadata<LlmEmbeddingRequest>();
    public static JsonTypeInfo<LlmEmbeddingResult> LlmEmbeddingResult => Metadata<LlmEmbeddingResult>();
    public static JsonTypeInfo<LlmEmbeddingUsage> LlmEmbeddingUsage => Metadata<LlmEmbeddingUsage>();
    public static JsonTypeInfo<LlmAgentContext> AgentContext => Metadata<LlmAgentContext>();
    public static JsonTypeInfo<LlmAgentContextEntry> AgentContextEntry => Metadata<LlmAgentContextEntry>();
    public static JsonTypeInfo<LlmAgentContextStatus> AgentContextStatus => Metadata<LlmAgentContextStatus>();
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(LlmAgentContextJsonConverter)])]
[JsonSerializable(typeof(LlmConnection))]
[JsonSerializable(typeof(LlmAccountUsageSnapshot))]
[JsonSerializable(typeof(LlmSetupConfiguration))]
[JsonSerializable(typeof(LlmChatRequest))]
[JsonSerializable(typeof(LlmChatResult))]
[JsonSerializable(typeof(LlmChatMessage))]
[JsonSerializable(typeof(LlmTokenization))]
[JsonSerializable(typeof(LlmToken))]
[JsonSerializable(typeof(LlmEmbeddingRequest))]
[JsonSerializable(typeof(LlmEmbeddingResult))]
[JsonSerializable(typeof(LlmEmbeddingUsage))]
[JsonSerializable(typeof(LlmAgentContext))]
[JsonSerializable(typeof(AgentContextFormat3))]
[JsonSerializable(typeof(LegacyAgentContext))]
[JsonSerializable(typeof(LlmAgentContextEntry))]
[JsonSerializable(typeof(LlmAgentContextStatus))]
[JsonSerializable(typeof(ConnectContinuationScopeIdentity))]
internal sealed partial class ConnectLlmJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, GenerationMode = JsonSourceGenerationMode.Metadata,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LlmCredential))]
internal sealed partial class LlmCredentialJsonContext : JsonSerializerContext;

internal sealed record ConnectContinuationScopeIdentity(LlmTransport Transport, string Endpoint, string? ApiKey,
    string? OAuthIdentity, IReadOnlyList<ConnectContinuationScopeHeader> Headers,
    IReadOnlyList<ConnectContinuationScopeHeader>? SecretHeaders,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LlmAzureConfiguration? Azure = null);
internal sealed record ConnectContinuationScopeHeader(string Name, string Value);
