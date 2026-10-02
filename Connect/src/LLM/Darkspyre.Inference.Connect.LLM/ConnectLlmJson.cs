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
    public static JsonTypeInfo<XaiMultiAgentControls> XaiMultiAgentControls => Metadata<XaiMultiAgentControls>();
    public static JsonTypeInfo<LlmAgentCapabilities> LlmAgentCapabilities => Metadata<LlmAgentCapabilities>();
    public static JsonTypeInfo<LlmAzureConfiguration> LlmAzureConfiguration => Metadata<LlmAzureConfiguration>();
    public static JsonTypeInfo<LlmAzureApiMode> LlmAzureApiMode => Metadata<LlmAzureApiMode>();
    public static JsonTypeInfo<LlmAzureAuthenticationMode> LlmAzureAuthenticationMode => Metadata<LlmAzureAuthenticationMode>();
    public static JsonTypeInfo<LlmAzureCloud> LlmAzureCloud => Metadata<LlmAzureCloud>();
    public static JsonTypeInfo<LlmAzureTokenAudience> LlmAzureTokenAudience => Metadata<LlmAzureTokenAudience>();
    public static JsonTypeInfo<LlmAzureEntraConfiguration> LlmAzureEntraConfiguration => Metadata<LlmAzureEntraConfiguration>();
    public static JsonTypeInfo<LlmAzureIdentitySelection> LlmAzureIdentitySelection => Metadata<LlmAzureIdentitySelection>();
    public static JsonTypeInfo<LlmAzureIdentitySelection.ServicePrincipalSecret> ServicePrincipalSecret => Metadata<LlmAzureIdentitySelection.ServicePrincipalSecret>();
    public static JsonTypeInfo<LlmAzureIdentitySelection.ServicePrincipalCertificate> ServicePrincipalCertificate => Metadata<LlmAzureIdentitySelection.ServicePrincipalCertificate>();
    public static JsonTypeInfo<LlmAzureIdentitySelection.WorkloadIdentity> WorkloadIdentity => Metadata<LlmAzureIdentitySelection.WorkloadIdentity>();
    public static JsonTypeInfo<LlmAzureIdentitySelection.SystemAssignedManagedIdentity> SystemAssignedManagedIdentity => Metadata<LlmAzureIdentitySelection.SystemAssignedManagedIdentity>();
    public static JsonTypeInfo<LlmAzureIdentitySelection.UserAssignedManagedIdentity> UserAssignedManagedIdentity => Metadata<LlmAzureIdentitySelection.UserAssignedManagedIdentity>();
    public static JsonTypeInfo<LlmAzureManagedIdentityId> LlmAzureManagedIdentityId => Metadata<LlmAzureManagedIdentityId>();
    public static JsonTypeInfo<LlmAzureManagedIdentityId.ClientId> ClientId => Metadata<LlmAzureManagedIdentityId.ClientId>();
    public static JsonTypeInfo<LlmAzureManagedIdentityId.ResourceId> ResourceId => Metadata<LlmAzureManagedIdentityId.ResourceId>();
    public static JsonTypeInfo<LlmAzureManagedIdentityId.ObjectId> ObjectId => Metadata<LlmAzureManagedIdentityId.ObjectId>();
    public static JsonTypeInfo<LlmAzureSecret> LlmAzureSecret => LlmCredentialJsonContext.Default.LlmAzureSecret;
    public static JsonTypeInfo<LlmAzureSecret.ClientSecret> ClientSecret => LlmCredentialJsonContext.Default.ClientSecret;
    public static JsonTypeInfo<LlmAzureSecret.CertificatePkcs12> CertificatePkcs12 => LlmCredentialJsonContext.Default.CertificatePkcs12;
    public static JsonTypeInfo<LlmAgentRuntimeProfile> LlmAgentRuntimeProfile => Metadata<LlmAgentRuntimeProfile>();
    public static JsonTypeInfo<LlmAgentRuntimeStateConfiguration> LlmAgentRuntimeStateConfiguration => Metadata<LlmAgentRuntimeStateConfiguration>();
    public static JsonTypeInfo<LlmAgentIsolatedRuntimeState> LlmAgentIsolatedRuntimeState => Metadata<LlmAgentIsolatedRuntimeState>();
    public static JsonTypeInfo<LlmAgentAdoptedRuntimeState> LlmAgentAdoptedRuntimeState => Metadata<LlmAgentAdoptedRuntimeState>();
    public static JsonTypeInfo<LlmAcpConfiguration> LlmAcpConfiguration => Metadata<LlmAcpConfiguration>();
    public static JsonTypeInfo<LlmAcpToolRelayConfiguration> LlmAcpToolRelayConfiguration => Metadata<LlmAcpToolRelayConfiguration>();
    public static JsonTypeInfo<LlmCopilotEndpointMode> LlmCopilotEndpointMode => Metadata<LlmCopilotEndpointMode>();
    public static JsonTypeInfo<LlmCopilotConfiguration> LlmCopilotConfiguration => Metadata<LlmCopilotConfiguration>();
    public static JsonTypeInfo<LlmAcpAuthenticationConfiguration> LlmAcpAuthenticationConfiguration => Metadata<LlmAcpAuthenticationConfiguration>();
    public static JsonTypeInfo<LlmAcpExternalAuthentication> LlmAcpExternalAuthentication => Metadata<LlmAcpExternalAuthentication>();
    public static JsonTypeInfo<LlmAcpAgentAuthentication> LlmAcpAgentAuthentication => Metadata<LlmAcpAgentAuthentication>();
    public static JsonTypeInfo<LlmAcpTerminalAuthentication> LlmAcpTerminalAuthentication => Metadata<LlmAcpTerminalAuthentication>();
    public static JsonTypeInfo<LlmAcpAuthenticationFlow> LlmAcpAuthenticationFlow => Metadata<LlmAcpAuthenticationFlow>();
    public static JsonTypeInfo<LlmAcpAuthenticationMethod> LlmAcpAuthenticationMethod => Metadata<LlmAcpAuthenticationMethod>();
    public static JsonTypeInfo<LlmCopilotAuthenticationConfiguration> LlmCopilotAuthenticationConfiguration => Metadata<LlmCopilotAuthenticationConfiguration>();
    public static JsonTypeInfo<LlmCopilotExplicitGitHubToken> LlmCopilotExplicitGitHubToken => Metadata<LlmCopilotExplicitGitHubToken>();
    public static JsonTypeInfo<LlmCopilotRuntimeAccount> LlmCopilotRuntimeAccount => Metadata<LlmCopilotRuntimeAccount>();
    public static JsonTypeInfo<LlmAgentRuntimeLoginScope> LlmAgentRuntimeLoginScope => Metadata<LlmAgentRuntimeLoginScope>();
    public static JsonTypeInfo<LlmAgentRuntimeAuthenticationSnapshot> LlmAgentRuntimeAuthenticationSnapshot => Metadata<LlmAgentRuntimeAuthenticationSnapshot>();
    public static JsonTypeInfo<LlmAgentRuntimeLoginOptions> LlmAgentRuntimeLoginOptions => Metadata<LlmAgentRuntimeLoginOptions>();
    public static JsonTypeInfo<LlmAgentStagedLoginState> LlmAgentStagedLoginState => Metadata<LlmAgentStagedLoginState>();
    public static JsonTypeInfo<LlmAgentRuntimeLoginResult> LlmAgentRuntimeLoginResult => Metadata<LlmAgentRuntimeLoginResult>();
    public static JsonTypeInfo<LlmAgentStagedStateDisposition> LlmAgentStagedStateDisposition => Metadata<LlmAgentStagedStateDisposition>();
    public static JsonTypeInfo<LlmAgentStagedStateSnapshot> LlmAgentStagedStateSnapshot => Metadata<LlmAgentStagedStateSnapshot>();
    public static JsonTypeInfo<LlmAgentStagedStateCommitResult> LlmAgentStagedStateCommitResult => Metadata<LlmAgentStagedStateCommitResult>();
    public static JsonTypeInfo<LlmCopilotCredentials> LlmCopilotCredentials => LlmCredentialJsonContext.Default.LlmCopilotCredentials;
    public static JsonTypeInfo<LlmCopilotRuntimeConnectionCredential> LlmCopilotRuntimeConnectionCredential => LlmCredentialJsonContext.Default.LlmCopilotRuntimeConnectionCredential;
    public static JsonTypeInfo<XaiStoragePublicUrlRequest> XaiStoragePublicUrlRequest => Metadata<XaiStoragePublicUrlRequest>();
    public static JsonTypeInfo<XaiStoragePublicUrlFlag> XaiStoragePublicUrlFlag => Metadata<XaiStoragePublicUrlFlag>();
    public static JsonTypeInfo<XaiStoragePublicUrlConfiguration> XaiStoragePublicUrlConfiguration => Metadata<XaiStoragePublicUrlConfiguration>();
    public static JsonTypeInfo<XaiFileStorageOptions> XaiFileStorageOptions => Metadata<XaiFileStorageOptions>();
    public static JsonTypeInfo<XaiConnectionSelection> XaiConnectionSelection => Metadata<XaiConnectionSelection>();
    public static JsonTypeInfo<LlmFileOrder> LlmFileOrder => Metadata<LlmFileOrder>();
    public static JsonTypeInfo<LlmFileSort> LlmFileSort => Metadata<LlmFileSort>();
    public static JsonTypeInfo<LlmFileContentFormat> LlmFileContentFormat => Metadata<LlmFileContentFormat>();
    public static JsonTypeInfo<LlmProviderFile> LlmProviderFile => Metadata<LlmProviderFile>();
    public static JsonTypeInfo<LlmFileUploadOptions> LlmFileUploadOptions => Metadata<LlmFileUploadOptions>();
    public static JsonTypeInfo<LlmFileListRequest> LlmFileListRequest => Metadata<LlmFileListRequest>();
    public static JsonTypeInfo<LlmFilePage> LlmFilePage => Metadata<LlmFilePage>();
    public static JsonTypeInfo<LlmFileDeletion> LlmFileDeletion => Metadata<LlmFileDeletion>();
    public static JsonTypeInfo<LlmFileDownloadOptions> LlmFileDownloadOptions => Metadata<LlmFileDownloadOptions>();
    public static JsonTypeInfo<ResourceDownloadReceipt> ResourceDownloadReceipt => Metadata<ResourceDownloadReceipt>();
    public static JsonTypeInfo<XaiPublicUrlOptions> XaiPublicUrlOptions => Metadata<XaiPublicUrlOptions>();
    public static JsonTypeInfo<XaiPublicUrl> XaiPublicUrl => Metadata<XaiPublicUrl>();
    public static JsonTypeInfo<XaiPublicUrlRevocation> XaiPublicUrlRevocation => Metadata<XaiPublicUrlRevocation>();
    public static JsonTypeInfo<LlmFileCapabilities> LlmFileCapabilities => Metadata<LlmFileCapabilities>();
    public static JsonTypeInfo<XaiSkill> XaiSkill => Metadata<XaiSkill>();
    public static JsonTypeInfo<XaiSkillDeletion> XaiSkillDeletion => Metadata<XaiSkillDeletion>();
    public static JsonTypeInfo<XaiSkillListRequest> XaiSkillListRequest => Metadata<XaiSkillListRequest>();
    public static JsonTypeInfo<XaiSkillPage> XaiSkillPage => Metadata<XaiSkillPage>();
    public static JsonTypeInfo<XaiSkillCapabilities> XaiSkillCapabilities => Metadata<XaiSkillCapabilities>();
    public static JsonTypeInfo<LlmContextCacheCreate> LlmContextCacheCreate => Metadata<LlmContextCacheCreate>();
    public static JsonTypeInfo<LlmContextCacheCreateCorrelation> LlmContextCacheCreateCorrelation => Metadata<LlmContextCacheCreateCorrelation>();
    public static JsonTypeInfo<LlmContextCache> LlmContextCache => Metadata<LlmContextCache>();
    public static JsonTypeInfo<LlmContextCacheUsage> LlmContextCacheUsage => Metadata<LlmContextCacheUsage>();
    public static JsonTypeInfo<LlmContextCacheList> LlmContextCacheList => Metadata<LlmContextCacheList>();
    public static JsonTypeInfo<LlmContextCachePage> LlmContextCachePage => Metadata<LlmContextCachePage>();
    public static JsonTypeInfo<LlmContextCacheDeleteResult> LlmContextCacheDeleteResult => Metadata<LlmContextCacheDeleteResult>();
    public static JsonTypeInfo<LlmAgentShellRequest> LlmAgentShellRequest => Metadata<LlmAgentShellRequest>();
    public static JsonTypeInfo<LlmAgentCallerServiceKind> LlmAgentCallerServiceKind => Metadata<LlmAgentCallerServiceKind>();
    public static JsonTypeInfo<LlmAgentPermissionChoiceKind> LlmAgentPermissionChoiceKind => Metadata<LlmAgentPermissionChoiceKind>();
    public static JsonTypeInfo<LlmAgentPermissionResult> LlmAgentPermissionResult => Metadata<LlmAgentPermissionResult>();
    public static JsonTypeInfo<LlmAgentToolPresentationContent> LlmAgentToolPresentationContent => Metadata<LlmAgentToolPresentationContent>();
    public static JsonTypeInfo<LlmAgentPresentedContentBlock> LlmAgentPresentedContentBlock => Metadata<LlmAgentPresentedContentBlock>();
    public static JsonTypeInfo<LlmAgentPresentedResourceBody> LlmAgentPresentedResourceBody => Metadata<LlmAgentPresentedResourceBody>();
    public static JsonTypeInfo<LlmAgentPermissionCancelled> LlmAgentPermissionCancelled => Metadata<LlmAgentPermissionCancelled>();
    public static JsonTypeInfo<LlmAgentCallbackScope> LlmAgentCallbackScope => Metadata<LlmAgentCallbackScope>();
    public static JsonTypeInfo<LlmAgentPermissionChoice> LlmAgentPermissionChoice => Metadata<LlmAgentPermissionChoice>();
    public static JsonTypeInfo<LlmAgentPermissionRequest> LlmAgentPermissionRequest => Metadata<LlmAgentPermissionRequest>();
    public static JsonTypeInfo<LlmAgentPermissionSelected> LlmAgentPermissionSelected => Metadata<LlmAgentPermissionSelected>();
    public static JsonTypeInfo<LlmAgentToolLocation> LlmAgentToolLocation => Metadata<LlmAgentToolLocation>();
    public static JsonTypeInfo<LlmAgentToolPresentedContent> LlmAgentToolPresentedContent => Metadata<LlmAgentToolPresentedContent>();
    public static JsonTypeInfo<LlmAgentToolPresentedDiff> LlmAgentToolPresentedDiff => Metadata<LlmAgentToolPresentedDiff>();
    public static JsonTypeInfo<LlmAgentToolPresentedTerminal> LlmAgentToolPresentedTerminal => Metadata<LlmAgentToolPresentedTerminal>();
    public static JsonTypeInfo<LlmAgentContentAnnotations> LlmAgentContentAnnotations => Metadata<LlmAgentContentAnnotations>();
    public static JsonTypeInfo<LlmAgentPresentedText> LlmAgentPresentedText => Metadata<LlmAgentPresentedText>();
    public static JsonTypeInfo<LlmAgentPresentedImage> LlmAgentPresentedImage => Metadata<LlmAgentPresentedImage>();
    public static JsonTypeInfo<LlmAgentPresentedAudio> LlmAgentPresentedAudio => Metadata<LlmAgentPresentedAudio>();
    public static JsonTypeInfo<LlmAgentPresentedResourceLink> LlmAgentPresentedResourceLink => Metadata<LlmAgentPresentedResourceLink>();
    public static JsonTypeInfo<LlmAgentPresentedResourceText> LlmAgentPresentedResourceText => Metadata<LlmAgentPresentedResourceText>();
    public static JsonTypeInfo<LlmAgentPresentedResourceBytes> LlmAgentPresentedResourceBytes => Metadata<LlmAgentPresentedResourceBytes>();
    public static JsonTypeInfo<LlmAgentPresentedEmbeddedResource> LlmAgentPresentedEmbeddedResource => Metadata<LlmAgentPresentedEmbeddedResource>();
    public static JsonTypeInfo<LlmAgentTextFileReadRequest> LlmAgentTextFileReadRequest => Metadata<LlmAgentTextFileReadRequest>();
    public static JsonTypeInfo<LlmAgentTextFileWriteRequest> LlmAgentTextFileWriteRequest => Metadata<LlmAgentTextFileWriteRequest>();
    public static JsonTypeInfo<LlmAgentTextFileWriteResult> LlmAgentTextFileWriteResult => Metadata<LlmAgentTextFileWriteResult>();
    public static JsonTypeInfo<LlmAgentEnvironmentVariable> LlmAgentEnvironmentVariable => Metadata<LlmAgentEnvironmentVariable>();
    public static JsonTypeInfo<LlmAgentTerminalCreateRequest> LlmAgentTerminalCreateRequest => Metadata<LlmAgentTerminalCreateRequest>();
    public static JsonTypeInfo<LlmAgentTerminalExit> LlmAgentTerminalExit => Metadata<LlmAgentTerminalExit>();
    public static JsonTypeInfo<LlmAgentTerminalOutput> LlmAgentTerminalOutput => Metadata<LlmAgentTerminalOutput>();
    public static JsonTypeInfo<LlmAgentToolPresentation> LlmAgentToolPresentation => Metadata<LlmAgentToolPresentation>();
    public static JsonTypeInfo<LlmAgentRuntimePolicy> LlmAgentRuntimePolicy => Metadata<LlmAgentRuntimePolicy>();
    public static JsonTypeInfo<LlmAgentHostRuntimePolicy> LlmAgentHostRuntimePolicy => Metadata<LlmAgentHostRuntimePolicy>();
    public static JsonTypeInfo<LlmAgentProviderRuntimePolicy> LlmAgentProviderRuntimePolicy => Metadata<LlmAgentProviderRuntimePolicy>();
    public static JsonTypeInfo<LlmAgentRuntimePolicyKind> LlmAgentRuntimePolicyKind => Metadata<LlmAgentRuntimePolicyKind>();
    public static JsonTypeInfo<LlmAgentClientToolDefinition> LlmAgentClientToolDefinition => Metadata<LlmAgentClientToolDefinition>();
    public static JsonTypeInfo<LlmAgentToolDefinition> LlmAgentToolDefinition => Metadata<LlmAgentToolDefinition>();
    public static JsonTypeInfo<LlmAgentShellTool> LlmAgentShellTool => Metadata<LlmAgentShellTool>();
    private static readonly JsonSerializerOptions OwnedOptions = CreateOptions();

    private static JsonTypeInfo<T> Metadata<T>() => OwnedOptions.GetTypeInfo<T>();

    private static JsonSerializerOptions CreateOptions()
    {
        // Retain generated converters and compose Common's exact live-authority refusal policy.
        var options = new JsonSerializerOptions(ConnectLlmJsonContext.Default.Options) { TypeInfoResolver = new HostedTypeInfoResolver() };
        foreach (var type in new[] { typeof(IInferenceResourceAccess), typeof(IInferenceResourceReadHandle), typeof(IInferenceResourceWriteHandle), typeof(IProviderAuthorization), typeof(LlmHostedToolKind), typeof(LlmHostedToolPhase), typeof(LlmImageGenerationAction) })
        {
            options.Converters.Add(InferenceJson.InferenceResourceReference.Options.Converters.Single(converter => converter.CanConvert(type)));
        }
        options.Converters.Add(new DocumentRankingMetricJsonConverter());
        options.Converters.Add(new DocumentRagPipelineJsonConverter());
        options.Converters.Add(new CallerEnumConverter<LlmAgentCallerServiceKind>());
        options.Converters.Add(new CallerEnumConverter<LlmAgentPermissionChoiceKind>());
        options.Converters.Add(new CallerEnumConverter<LlmAgentRuntimePolicyKind>());
        options.Converters.Add(new CallerEnumConverter<LlmAzureApiMode>());
        options.Converters.Add(new CallerEnumConverter<LlmAzureAuthenticationMode>());
        options.Converters.Add(new CallerEnumConverter<LlmAzureCloud>());
        options.Converters.Add(new CallerEnumConverter<LlmAzureTokenAudience>());
        options.Converters.Add(new CallerEnumConverter<LlmCopilotEndpointMode>());
        options.Converters.Add(new CallerEnumConverter<LlmAcpAuthenticationFlow>());
        options.Converters.Add(new CallerEnumConverter<LlmAgentStagedStateDisposition>());
        options.Converters.Add(new FileOrderJsonConverter());
        options.Converters.Add(new FileSortJsonConverter());
        options.Converters.Add(new FileContentFormatJsonConverter());
        options.Converters.Add(new FileRuntimeJsonConverter<XaiSkillUpload>());
        options.Converters.Add(new FileRuntimeJsonConverter<XaiSkillZipUpload>());
        options.Converters.Add(new FileRuntimeJsonConverter<XaiSkillDirectoryFile>());
        options.Converters.Add(new FileRuntimeJsonConverter<XaiSkillDirectoryUpload>());
        options.Converters.Add(new FileRuntimeJsonConverter<ILlmFileClient>());
        options.Converters.Add(new FileRuntimeJsonConverter<IXaiFilePublicUrlClient>());
        options.Converters.Add(new FileRuntimeJsonConverter<IXaiSkillClient>());
        options.Converters.Add(new FileRuntimeJsonConverter<IDocumentSearchClient>("Live document-search clients are not portable data."));
        options.MakeReadOnly();
        return options;
    }

    private sealed class HostedTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (LlmAgentCallerJson.RuntimeMetadata(type, options) is { } runtime) return runtime;
            if (LlmRuntimeAuthenticationJson.RuntimeMetadata(type, options) is { } authentication) return authentication;
            var metadata = ((IJsonTypeInfoResolver)ConnectLlmJsonContext.Default).GetTypeInfo(type, options);
            if (type == typeof(LlmProviderControls) && metadata is not null)
                metadata.PolymorphismOptions = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = "type",
                    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                    DerivedTypes = { new JsonDerivedType(typeof(XaiMultiAgentControls), "xai-multi-agent") }
                };
            if (type == typeof(LlmAgentRuntimePolicy) && metadata is not null)
                metadata.PolymorphismOptions = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = "type",
                    DerivedTypes =
                    {
                        new JsonDerivedType(typeof(LlmAgentHostRuntimePolicy), "host-controlled"),
                        new JsonDerivedType(typeof(LlmAgentProviderRuntimePolicy), "provider-configured")
                    }
                };
            if (type == typeof(LlmHostedTool) && metadata?.PolymorphismOptions is { } polymorphism)
            {
                polymorphism.DerivedTypes.Add(new JsonDerivedType(typeof(XaiXSearchTool), "xai-x-search"));
            }
            if (metadata is not null && (type == typeof(LlmHostedImageOutput) || type == typeof(LlmAgentPresentedImage)
                || type == typeof(LlmAgentPresentedAudio) || type == typeof(LlmAgentPresentedResourceBytes)))
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
    public static JsonTypeInfo<LlmAgentResourceSelectionChangedEvent> LlmAgentResourceSelectionChangedEvent => Metadata<LlmAgentResourceSelectionChangedEvent>();
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
    Converters = [typeof(LlmAgentContextJsonConverter), typeof(XaiFamilyBindingJsonConverter),
        typeof(CallerAuthorityConverter<LlmAgentRuntimeLaunchInputs>),
        typeof(CallerAuthorityConverter<LlmAgentRuntimeLoginServices>),
        typeof(CallerAuthorityConverter<ILlmAgentRuntimeLoginTerminalFactory>),
        typeof(CallerAuthorityConverter<ILlmAgentRuntimeAuthentication>),
        typeof(CallerAuthorityConverter<LlmAgentRuntimeLoginTerminalRequest>),
        typeof(CallerAuthorityConverter<ILlmAuthPrompt>)])]
[JsonSerializable(typeof(LlmAzureConfiguration))]
[JsonSerializable(typeof(LlmAzureApiMode))]
[JsonSerializable(typeof(LlmAzureAuthenticationMode))]
[JsonSerializable(typeof(LlmAzureCloud))]
[JsonSerializable(typeof(LlmAzureTokenAudience))]
[JsonSerializable(typeof(LlmAzureEntraConfiguration))]
[JsonSerializable(typeof(LlmAzureIdentitySelection))]
[JsonSerializable(typeof(LlmAzureIdentitySelection.ServicePrincipalSecret))]
[JsonSerializable(typeof(LlmAzureIdentitySelection.ServicePrincipalCertificate))]
[JsonSerializable(typeof(LlmAzureIdentitySelection.WorkloadIdentity))]
[JsonSerializable(typeof(LlmAzureIdentitySelection.SystemAssignedManagedIdentity))]
[JsonSerializable(typeof(LlmAzureIdentitySelection.UserAssignedManagedIdentity))]
[JsonSerializable(typeof(LlmAzureManagedIdentityId))]
[JsonSerializable(typeof(LlmAzureManagedIdentityId.ClientId))]
[JsonSerializable(typeof(LlmAzureManagedIdentityId.ResourceId))]
[JsonSerializable(typeof(LlmAzureManagedIdentityId.ObjectId))]
[JsonSerializable(typeof(LlmAgentRuntimeProfile))]
[JsonSerializable(typeof(LlmAgentCapabilities))]
[JsonSerializable(typeof(LlmAgentRuntimeStateConfiguration))]
[JsonSerializable(typeof(LlmAgentIsolatedRuntimeState))]
[JsonSerializable(typeof(LlmAgentAdoptedRuntimeState))]
[JsonSerializable(typeof(LlmAcpConfiguration))]
[JsonSerializable(typeof(LlmAcpToolRelayConfiguration))]
[JsonSerializable(typeof(LlmCopilotEndpointMode))]
[JsonSerializable(typeof(LlmCopilotConfiguration))]
[JsonSerializable(typeof(LlmAcpAuthenticationConfiguration))]
[JsonSerializable(typeof(LlmAcpExternalAuthentication))]
[JsonSerializable(typeof(LlmAcpAgentAuthentication))]
[JsonSerializable(typeof(LlmAcpTerminalAuthentication))]
[JsonSerializable(typeof(LlmAcpAuthenticationFlow))]
[JsonSerializable(typeof(LlmAcpAuthenticationMethod))]
[JsonSerializable(typeof(LlmCopilotAuthenticationConfiguration))]
[JsonSerializable(typeof(LlmCopilotExplicitGitHubToken))]
[JsonSerializable(typeof(LlmCopilotRuntimeAccount))]
[JsonSerializable(typeof(LlmAgentRuntimeLoginScope))]
[JsonSerializable(typeof(LlmAgentRuntimeAuthenticationSnapshot))]
[JsonSerializable(typeof(LlmAgentRuntimeLoginOptions))]
[JsonSerializable(typeof(LlmAgentStagedLoginState))]
[JsonSerializable(typeof(LlmAgentRuntimeLoginResult))]
[JsonSerializable(typeof(LlmAgentStagedStateDisposition))]
[JsonSerializable(typeof(LlmAgentStagedStateSnapshot))]
[JsonSerializable(typeof(LlmAgentStagedStateCommitResult))]
[JsonSerializable(typeof(LlmConnection))]
[JsonSerializable(typeof(XaiMultiAgentControls))]
[JsonSerializable(typeof(LlmAgentTurnRequest))]
[JsonSerializable(typeof(XaiStoragePublicUrlRequest))]
[JsonSerializable(typeof(XaiStoragePublicUrlFlag))]
[JsonSerializable(typeof(XaiStoragePublicUrlConfiguration))]
[JsonSerializable(typeof(XaiFileStorageOptions))]
[JsonSerializable(typeof(LlmFileOrder))]
[JsonSerializable(typeof(LlmFileSort))]
[JsonSerializable(typeof(LlmFileContentFormat))]
[JsonSerializable(typeof(LlmProviderFile))]
[JsonSerializable(typeof(LlmFileUploadOptions))]
[JsonSerializable(typeof(LlmFileListRequest))]
[JsonSerializable(typeof(LlmFilePage))]
[JsonSerializable(typeof(LlmFileDeletion))]
[JsonSerializable(typeof(LlmFileDownloadOptions))]
[JsonSerializable(typeof(ResourceDownloadReceipt))]
[JsonSerializable(typeof(XaiPublicUrlOptions))]
[JsonSerializable(typeof(XaiPublicUrl))]
[JsonSerializable(typeof(XaiPublicUrlRevocation))]
[JsonSerializable(typeof(LlmFileCapabilities))]
[JsonSerializable(typeof(XaiSkill))]
[JsonSerializable(typeof(XaiSkillDeletion))]
[JsonSerializable(typeof(XaiSkillListRequest))]
[JsonSerializable(typeof(XaiSkillPage))]
[JsonSerializable(typeof(XaiSkillCapabilities))]
[JsonSerializable(typeof(LlmContextCacheCreate))]
[JsonSerializable(typeof(LlmContextCacheCreateCorrelation))]
[JsonSerializable(typeof(LlmContextCache))]
[JsonSerializable(typeof(LlmContextCacheUsage))]
[JsonSerializable(typeof(LlmContextCacheList))]
[JsonSerializable(typeof(LlmContextCachePage))]
[JsonSerializable(typeof(LlmContextCacheDeleteResult))]
[JsonSerializable(typeof(LlmAgentShellRequest))]
[JsonSerializable(typeof(LlmAgentCallerServiceKind))]
[JsonSerializable(typeof(LlmAgentPermissionChoiceKind))]
[JsonSerializable(typeof(LlmAgentPermissionResult))]
[JsonSerializable(typeof(LlmAgentToolPresentationContent))]
[JsonSerializable(typeof(LlmAgentPresentedContentBlock))]
[JsonSerializable(typeof(LlmAgentPresentedResourceBody))]
[JsonSerializable(typeof(LlmAgentPermissionCancelled))]
[JsonSerializable(typeof(LlmAgentCallbackScope))]
[JsonSerializable(typeof(LlmAgentPermissionChoice))]
[JsonSerializable(typeof(LlmAgentPermissionRequest))]
[JsonSerializable(typeof(LlmAgentPermissionSelected))]
[JsonSerializable(typeof(LlmAgentToolLocation))]
[JsonSerializable(typeof(LlmAgentToolPresentedContent))]
[JsonSerializable(typeof(LlmAgentToolPresentedDiff))]
[JsonSerializable(typeof(LlmAgentToolPresentedTerminal))]
[JsonSerializable(typeof(LlmAgentContentAnnotations))]
[JsonSerializable(typeof(LlmAgentPresentedText))]
[JsonSerializable(typeof(LlmAgentPresentedImage))]
[JsonSerializable(typeof(LlmAgentPresentedAudio))]
[JsonSerializable(typeof(LlmAgentPresentedResourceLink))]
[JsonSerializable(typeof(LlmAgentPresentedResourceText))]
[JsonSerializable(typeof(LlmAgentPresentedResourceBytes))]
[JsonSerializable(typeof(LlmAgentPresentedEmbeddedResource))]
[JsonSerializable(typeof(LlmAgentTextFileReadRequest))]
[JsonSerializable(typeof(LlmAgentTextFileWriteRequest))]
[JsonSerializable(typeof(LlmAgentTextFileWriteResult))]
[JsonSerializable(typeof(LlmAgentEnvironmentVariable))]
[JsonSerializable(typeof(LlmAgentTerminalCreateRequest))]
[JsonSerializable(typeof(LlmAgentTerminalExit))]
[JsonSerializable(typeof(LlmAgentTerminalOutput))]
[JsonSerializable(typeof(LlmAgentToolPresentation))]
[JsonSerializable(typeof(LlmAgentRuntimePolicy))]
[JsonSerializable(typeof(LlmAgentHostRuntimePolicy))]
[JsonSerializable(typeof(LlmAgentProviderRuntimePolicy))]
[JsonSerializable(typeof(LlmAgentRuntimePolicyKind))]
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
[JsonSerializable(typeof(LlmAgentResourceSelectionChangedEvent))]
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
[JsonSerializable(typeof(LlmAzureSecret))]
[JsonSerializable(typeof(LlmAzureSecret.ClientSecret))]
[JsonSerializable(typeof(LlmAzureSecret.CertificatePkcs12))]
[JsonSerializable(typeof(LlmCopilotCredentials))]
[JsonSerializable(typeof(LlmCopilotRuntimeConnectionCredential))]
internal sealed partial class LlmCredentialJsonContext : JsonSerializerContext;

internal sealed record ConnectContinuationScopeIdentity(LlmTransport Transport, string Endpoint, string? ApiKey,
    string? OAuthIdentity, IReadOnlyList<ConnectContinuationScopeHeader> Headers,
    IReadOnlyList<ConnectContinuationScopeHeader>? SecretHeaders,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LlmAzureConfiguration? Azure = null);
internal sealed record ConnectContinuationScopeHeader(string Name, string Value);
