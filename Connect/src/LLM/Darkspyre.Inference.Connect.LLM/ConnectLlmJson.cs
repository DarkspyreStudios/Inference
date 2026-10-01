using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Connect-owned metadata for closed connection, request and captured-context contracts.</summary>
public static class ConnectLlmJson
{
    public static JsonTypeInfo<XaiConnectionSelection> XaiConnectionSelection => ConnectLlmJsonContext.Default.XaiConnectionSelection;
    public static JsonTypeInfo<LlmCredential> Credential => LlmCredentialJsonContext.Default.LlmCredential;
    public static JsonTypeInfo<LlmAccountUsageSnapshot> AccountUsageSnapshot => ConnectLlmJsonContext.Default.LlmAccountUsageSnapshot;
    public static JsonTypeInfo<LlmConnection> Connection => ConnectLlmJsonContext.Default.LlmConnection;
    public static JsonTypeInfo<LlmSetupConfiguration> SetupConfiguration => ConnectLlmJsonContext.Default.LlmSetupConfiguration;
    public static JsonTypeInfo<LlmChatRequest> ChatRequest => ConnectLlmJsonContext.Default.LlmChatRequest;
    public static JsonTypeInfo<LlmChatResult> ChatResult => ConnectLlmJsonContext.Default.LlmChatResult;
    public static JsonTypeInfo<LlmChatMessage> ChatMessage => ConnectLlmJsonContext.Default.LlmChatMessage;
    public static JsonTypeInfo<LlmTokenization> Tokenization => ConnectLlmJsonContext.Default.LlmTokenization;
    public static JsonTypeInfo<LlmToken> Token => ConnectLlmJsonContext.Default.LlmToken;
    public static JsonTypeInfo<LlmEmbeddingRequest> LlmEmbeddingRequest => ConnectLlmJsonContext.Default.LlmEmbeddingRequest;
    public static JsonTypeInfo<LlmEmbeddingResult> LlmEmbeddingResult => ConnectLlmJsonContext.Default.LlmEmbeddingResult;
    public static JsonTypeInfo<LlmEmbeddingUsage> LlmEmbeddingUsage => ConnectLlmJsonContext.Default.LlmEmbeddingUsage;
    public static JsonTypeInfo<LlmAgentContext> AgentContext => ConnectLlmJsonContext.Default.LlmAgentContext;
    public static JsonTypeInfo<LlmAgentContextEntry> AgentContextEntry => ConnectLlmJsonContext.Default.LlmAgentContextEntry;
    public static JsonTypeInfo<LlmAgentContextStatus> AgentContextStatus => ConnectLlmJsonContext.Default.LlmAgentContextStatus;
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(XaiFamilyBindingJsonConverter)])]
[JsonSerializable(typeof(LlmConnection))]
[JsonSerializable(typeof(XaiConnectionSelection))]
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
