using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Connect.LLM.Internal;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Connect-owned metadata for closed connection, request and captured-context contracts.</summary>
public static class ConnectLlmJson
{
    public static JsonTypeInfo<XaiConnectionSelection> XaiConnectionSelection => Metadata<XaiConnectionSelection>();
    public static JsonTypeInfo<LlmContextCacheCreate> LlmContextCacheCreate => Metadata<LlmContextCacheCreate>();
    public static JsonTypeInfo<LlmContextCacheCreateCorrelation> LlmContextCacheCreateCorrelation => Metadata<LlmContextCacheCreateCorrelation>();
    public static JsonTypeInfo<LlmContextCache> LlmContextCache => Metadata<LlmContextCache>();
    public static JsonTypeInfo<LlmContextCacheUsage> LlmContextCacheUsage => Metadata<LlmContextCacheUsage>();
    public static JsonTypeInfo<LlmContextCacheList> LlmContextCacheList => Metadata<LlmContextCacheList>();
    public static JsonTypeInfo<LlmContextCachePage> LlmContextCachePage => Metadata<LlmContextCachePage>();
    public static JsonTypeInfo<LlmContextCacheDeleteResult> LlmContextCacheDeleteResult => Metadata<LlmContextCacheDeleteResult>();
    public static JsonTypeInfo<LlmAgentClientToolDefinition> LlmAgentClientToolDefinition => Metadata<LlmAgentClientToolDefinition>();
    public static JsonTypeInfo<LlmAgentToolDefinition> LlmAgentToolDefinition => Metadata<LlmAgentToolDefinition>();
    public static JsonTypeInfo<LlmAgentShellTool> LlmAgentShellTool => Metadata<LlmAgentShellTool>();
    private static readonly JsonSerializerOptions OwnedOptions = CreateOptions();

    private static JsonTypeInfo<T> Metadata<T>() => OwnedOptions.GetTypeInfo<T>();

    private static JsonSerializerOptions CreateOptions()
    {
        // Retain generated converters and compose Common's exact live-authority refusal policy.
        var options = new JsonSerializerOptions(ConnectLlmJsonContext.Default.Options) { TypeInfoResolver = new HostedTypeInfoResolver() };
        foreach (var type in new[] { typeof(IInferenceResourceAccess), typeof(IInferenceResourceReadHandle), typeof(IInferenceResourceWriteHandle), typeof(IProviderAuthorization), typeof(LlmHostedToolPhase), typeof(LlmImageGenerationAction) })
        {
            options.Converters.Add(InferenceJson.InferenceResourceReference.Options.Converters.Single(converter => converter.CanConvert(type)));
        }
        options.Converters.Add(new DocumentRankingMetricJsonConverter());
        options.Converters.Add(new DocumentRagPipelineJsonConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed class HostedTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            var metadata = ((IJsonTypeInfoResolver)ConnectLlmJsonContext.Default).GetTypeInfo(type, options);
            if (type == typeof(LlmHostedTool) && metadata?.PolymorphismOptions is { } polymorphism)
            {
                polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(XaiXSearchTool), "xai-x-search"));
            }
            if (type == typeof(LlmHostedImageOutput) && metadata is not null)
                metadata.Properties.Single(property => property.Name == "Data").CustomConverter =
                    InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter;
            if (metadata is not null && (type == typeof(LlmAgentHostedToolCallStartedEvent) || type == typeof(LlmAgentHostedToolCallProgressEvent)
                || type == typeof(LlmAgentHostedToolCallCompletedEvent) || type == typeof(LlmAgentCitationEvent) || type == typeof(LlmAgentCompactedEvent)))
            {
                metadata.Properties.Single(property => property.Name == nameof(LlmAgentEvent.ThreadId)).IsRequired = true;
                metadata.Properties.Single(property => property.Name == nameof(LlmAgentEvent.TurnId)).IsRequired = true;
                static void ValidateScope(object value)
                {
                    var item = (LlmAgentEvent)value;
                    HostedEventFacts.Identity(item.ThreadId); HostedEventFacts.Identity(item.TurnId);
                }
                metadata.OnSerializing = ValidateScope;
                metadata.OnDeserialized = ValidateScope;
            }
            return metadata;
        }
    }

    public static JsonTypeInfo<LlmInferenceRequest> PortableRequest => Metadata<LlmInferenceRequest>();
    public static JsonTypeInfo<XaiXSearchTool> XaiXSearchTool => Metadata<XaiXSearchTool>();
    public static JsonTypeInfo<LlmAgentHostedToolCallStartedEvent> LlmAgentHostedToolCallStartedEvent => Metadata<LlmAgentHostedToolCallStartedEvent>();
    public static JsonTypeInfo<LlmAgentHostedToolCallProgressEvent> LlmAgentHostedToolCallProgressEvent => Metadata<LlmAgentHostedToolCallProgressEvent>();
    public static JsonTypeInfo<LlmAgentHostedToolCallCompletedEvent> LlmAgentHostedToolCallCompletedEvent => Metadata<LlmAgentHostedToolCallCompletedEvent>();
    public static JsonTypeInfo<LlmAgentCitationEvent> LlmAgentCitationEvent => Metadata<LlmAgentCitationEvent>();
    public static JsonTypeInfo<LlmAgentCompactedEvent> LlmAgentCompactedEvent => Metadata<LlmAgentCompactedEvent>();
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
    public static JsonTypeInfo<DocumentGroupAggregate> DocumentGroupAggregate => Metadata<DocumentGroupAggregate>();
    public static JsonTypeInfo<DocumentMinK> DocumentMinK => Metadata<DocumentMinK>();
    public static JsonTypeInfo<DocumentMaxK> DocumentMaxK => Metadata<DocumentMaxK>();
    public static JsonTypeInfo<DocumentSearchGroupBy> DocumentSearchGroupBy => Metadata<DocumentSearchGroupBy>();
    public static JsonTypeInfo<DocumentHybridReranker> DocumentHybridReranker => Metadata<DocumentHybridReranker>();
    public static JsonTypeInfo<DocumentRerankerModel> DocumentRerankerModel => Metadata<DocumentRerankerModel>();
    public static JsonTypeInfo<DocumentRrfReranker> DocumentRrfReranker => Metadata<DocumentRrfReranker>();
    public static JsonTypeInfo<DocumentRetrievalMode> DocumentRetrievalMode => Metadata<DocumentRetrievalMode>();
    public static JsonTypeInfo<DocumentHybridRetrieval> DocumentHybridRetrieval => Metadata<DocumentHybridRetrieval>();
    public static JsonTypeInfo<DocumentSemanticRetrieval> DocumentSemanticRetrieval => Metadata<DocumentSemanticRetrieval>();
    public static JsonTypeInfo<DocumentKeywordRetrieval> DocumentKeywordRetrieval => Metadata<DocumentKeywordRetrieval>();
    public static JsonTypeInfo<XaiDocumentRankingMetric> XaiDocumentRankingMetric => Metadata<XaiDocumentRankingMetric>();
    public static JsonTypeInfo<XaiDocumentRagPipeline> XaiDocumentRagPipeline => Metadata<XaiDocumentRagPipeline>();
    public static JsonTypeInfo<XaiDocumentSearchOptions> XaiDocumentSearchOptions => Metadata<XaiDocumentSearchOptions>();
    public static JsonTypeInfo<DocumentSearchRequest> DocumentSearchRequest => Metadata<DocumentSearchRequest>();
    public static JsonTypeInfo<DocumentSearchMatch> DocumentSearchMatch => Metadata<DocumentSearchMatch>();
    public static JsonTypeInfo<DocumentSearchResult> DocumentSearchResult => Metadata<DocumentSearchResult>();
    public static JsonTypeInfo<LlmAgentContext> AgentContext => Metadata<LlmAgentContext>();
    public static JsonTypeInfo<LlmAgentContextEntry> AgentContextEntry => Metadata<LlmAgentContextEntry>();
    public static JsonTypeInfo<LlmAgentContextStatus> AgentContextStatus => Metadata<LlmAgentContextStatus>();
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(LlmAgentContextJsonConverter), typeof(XaiFamilyBindingJsonConverter)])]
[JsonSerializable(typeof(LlmConnection))]
[JsonSerializable(typeof(LlmContextCacheCreate))]
[JsonSerializable(typeof(LlmContextCacheCreateCorrelation))]
[JsonSerializable(typeof(LlmContextCache))]
[JsonSerializable(typeof(LlmContextCacheUsage))]
[JsonSerializable(typeof(LlmContextCacheList))]
[JsonSerializable(typeof(LlmContextCachePage))]
[JsonSerializable(typeof(LlmContextCacheDeleteResult))]
[JsonSerializable(typeof(LlmAgentClientToolDefinition))]
[JsonSerializable(typeof(LlmAgentToolDefinition))]
[JsonSerializable(typeof(LlmAgentShellTool))]
[JsonSerializable(typeof(XaiConnectionSelection))]
[JsonSerializable(typeof(LlmAccountUsageSnapshot))]
[JsonSerializable(typeof(LlmSetupConfiguration))]
[JsonSerializable(typeof(LlmChatRequest))]
[JsonSerializable(typeof(LlmInferenceRequest))]
[JsonSerializable(typeof(LlmHostedTool))]
[JsonSerializable(typeof(XaiXSearchTool))]
[JsonSerializable(typeof(LlmAgentHostedToolCallStartedEvent))]
[JsonSerializable(typeof(LlmAgentHostedToolCallProgressEvent))]
[JsonSerializable(typeof(LlmAgentHostedToolCallCompletedEvent))]
[JsonSerializable(typeof(LlmAgentCitationEvent))]
[JsonSerializable(typeof(LlmAgentCompactedEvent))]
[JsonSerializable(typeof(LlmChatResult))]
[JsonSerializable(typeof(LlmChatMessage))]
[JsonSerializable(typeof(LlmTokenization))]
[JsonSerializable(typeof(LlmToken))]
[JsonSerializable(typeof(LlmEmbeddingRequest))]
[JsonSerializable(typeof(LlmEmbeddingResult))]
[JsonSerializable(typeof(LlmEmbeddingUsage))]
[JsonSerializable(typeof(DocumentGroupAggregate))]
[JsonSerializable(typeof(DocumentMinK))]
[JsonSerializable(typeof(DocumentMaxK))]
[JsonSerializable(typeof(DocumentSearchGroupBy))]
[JsonSerializable(typeof(DocumentHybridReranker))]
[JsonSerializable(typeof(DocumentRerankerModel))]
[JsonSerializable(typeof(DocumentRrfReranker))]
[JsonSerializable(typeof(DocumentRetrievalMode))]
[JsonSerializable(typeof(DocumentHybridRetrieval))]
[JsonSerializable(typeof(DocumentSemanticRetrieval))]
[JsonSerializable(typeof(DocumentKeywordRetrieval))]
[JsonSerializable(typeof(XaiDocumentRankingMetric))]
[JsonSerializable(typeof(XaiDocumentRagPipeline))]
[JsonSerializable(typeof(XaiDocumentSearchOptions))]
[JsonSerializable(typeof(DocumentSearchRequest))]
[JsonSerializable(typeof(DocumentSearchMatch))]
[JsonSerializable(typeof(DocumentSearchResult))]
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
