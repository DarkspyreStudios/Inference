using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <summary>JSON serializer metadata for the <c>LlmVertexCacheStaging</c> contract.</summary>
    public static JsonTypeInfo<LlmVertexCacheStaging> LlmVertexCacheStaging => Metadata<LlmVertexCacheStaging>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentCapabilities</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentCapabilities> LlmAgentCapabilities => Metadata<LlmAgentCapabilities>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureConfiguration</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureConfiguration> LlmAzureConfiguration => Metadata<LlmAzureConfiguration>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureApiMode</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureApiMode> LlmAzureApiMode => Metadata<LlmAzureApiMode>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureAuthenticationMode</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureAuthenticationMode> LlmAzureAuthenticationMode => Metadata<LlmAzureAuthenticationMode>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureCloud</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureCloud> LlmAzureCloud => Metadata<LlmAzureCloud>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureTokenAudience</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureTokenAudience> LlmAzureTokenAudience => Metadata<LlmAzureTokenAudience>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureEntraConfiguration</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureEntraConfiguration> LlmAzureEntraConfiguration => Metadata<LlmAzureEntraConfiguration>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureIdentitySelection</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureIdentitySelection> LlmAzureIdentitySelection => Metadata<LlmAzureIdentitySelection>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureIdentitySelection.ServicePrincipalSecret</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureIdentitySelection.ServicePrincipalSecret> ServicePrincipalSecret => Metadata<LlmAzureIdentitySelection.ServicePrincipalSecret>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureIdentitySelection.ServicePrincipalCertificate</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureIdentitySelection.ServicePrincipalCertificate> ServicePrincipalCertificate => Metadata<LlmAzureIdentitySelection.ServicePrincipalCertificate>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureIdentitySelection.WorkloadIdentity</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureIdentitySelection.WorkloadIdentity> WorkloadIdentity => Metadata<LlmAzureIdentitySelection.WorkloadIdentity>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureIdentitySelection.SystemAssignedManagedIdentity</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureIdentitySelection.SystemAssignedManagedIdentity> SystemAssignedManagedIdentity => Metadata<LlmAzureIdentitySelection.SystemAssignedManagedIdentity>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureIdentitySelection.UserAssignedManagedIdentity</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureIdentitySelection.UserAssignedManagedIdentity> UserAssignedManagedIdentity => Metadata<LlmAzureIdentitySelection.UserAssignedManagedIdentity>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureManagedIdentityId</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureManagedIdentityId> LlmAzureManagedIdentityId => Metadata<LlmAzureManagedIdentityId>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureManagedIdentityId.ClientId</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureManagedIdentityId.ClientId> ClientId => Metadata<LlmAzureManagedIdentityId.ClientId>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureManagedIdentityId.ResourceId</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureManagedIdentityId.ResourceId> ResourceId => Metadata<LlmAzureManagedIdentityId.ResourceId>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureManagedIdentityId.ObjectId</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureManagedIdentityId.ObjectId> ObjectId => Metadata<LlmAzureManagedIdentityId.ObjectId>();
    /// <summary>JSON serializer metadata for the <c>LlmAzureSecret</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureSecret> LlmAzureSecret => new LlmCredentialJsonContext().LlmAzureSecret;
    /// <summary>JSON serializer metadata for the <c>LlmAzureSecret.ClientSecret</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureSecret.ClientSecret> ClientSecret => new LlmCredentialJsonContext().ClientSecret;
    /// <summary>JSON serializer metadata for the <c>LlmAzureSecret.CertificatePkcs12</c> contract.</summary>
    public static JsonTypeInfo<LlmAzureSecret.CertificatePkcs12> CertificatePkcs12 => new LlmCredentialJsonContext().CertificatePkcs12;
    /// <summary>JSON serializer metadata for the <c>LlmAgentRuntimeProfile</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentRuntimeProfile> LlmAgentRuntimeProfile => Metadata<LlmAgentRuntimeProfile>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentRuntimeStateConfiguration</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentRuntimeStateConfiguration> LlmAgentRuntimeStateConfiguration => Metadata<LlmAgentRuntimeStateConfiguration>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentIsolatedRuntimeState</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentIsolatedRuntimeState> LlmAgentIsolatedRuntimeState => Metadata<LlmAgentIsolatedRuntimeState>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentAdoptedRuntimeState</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentAdoptedRuntimeState> LlmAgentAdoptedRuntimeState => Metadata<LlmAgentAdoptedRuntimeState>();
    /// <summary>JSON serializer metadata for the <c>LlmAcpConfiguration</c> contract.</summary>
    public static JsonTypeInfo<LlmAcpConfiguration> LlmAcpConfiguration => Metadata<LlmAcpConfiguration>();
    /// <summary>JSON serializer metadata for the <c>LlmAcpToolRelayConfiguration</c> contract.</summary>
    public static JsonTypeInfo<LlmAcpToolRelayConfiguration> LlmAcpToolRelayConfiguration => Metadata<LlmAcpToolRelayConfiguration>();
    /// <summary>JSON serializer metadata for the <c>LlmCopilotEndpointMode</c> contract.</summary>
    public static JsonTypeInfo<LlmCopilotEndpointMode> LlmCopilotEndpointMode => Metadata<LlmCopilotEndpointMode>();
    /// <summary>JSON serializer metadata for the <c>LlmCopilotConfiguration</c> contract.</summary>
    public static JsonTypeInfo<LlmCopilotConfiguration> LlmCopilotConfiguration => Metadata<LlmCopilotConfiguration>();
    /// <summary>JSON serializer metadata for the <c>LlmAcpAuthenticationConfiguration</c> contract.</summary>
    public static JsonTypeInfo<LlmAcpAuthenticationConfiguration> LlmAcpAuthenticationConfiguration => Metadata<LlmAcpAuthenticationConfiguration>();
    /// <summary>JSON serializer metadata for the <c>LlmAcpExternalAuthentication</c> contract.</summary>
    public static JsonTypeInfo<LlmAcpExternalAuthentication> LlmAcpExternalAuthentication => Metadata<LlmAcpExternalAuthentication>();
    /// <summary>JSON serializer metadata for the <c>LlmAcpAgentAuthentication</c> contract.</summary>
    public static JsonTypeInfo<LlmAcpAgentAuthentication> LlmAcpAgentAuthentication => Metadata<LlmAcpAgentAuthentication>();
    /// <summary>JSON serializer metadata for the <c>LlmAcpTerminalAuthentication</c> contract.</summary>
    public static JsonTypeInfo<LlmAcpTerminalAuthentication> LlmAcpTerminalAuthentication => Metadata<LlmAcpTerminalAuthentication>();
    /// <summary>JSON serializer metadata for the <c>LlmAcpAuthenticationFlow</c> contract.</summary>
    public static JsonTypeInfo<LlmAcpAuthenticationFlow> LlmAcpAuthenticationFlow => Metadata<LlmAcpAuthenticationFlow>();
    /// <summary>JSON serializer metadata for the <c>LlmAcpAuthenticationMethod</c> contract.</summary>
    public static JsonTypeInfo<LlmAcpAuthenticationMethod> LlmAcpAuthenticationMethod => Metadata<LlmAcpAuthenticationMethod>();
    /// <summary>JSON serializer metadata for the <c>LlmCopilotAuthenticationConfiguration</c> contract.</summary>
    public static JsonTypeInfo<LlmCopilotAuthenticationConfiguration> LlmCopilotAuthenticationConfiguration => Metadata<LlmCopilotAuthenticationConfiguration>();
    /// <summary>JSON serializer metadata for the <c>LlmCopilotExplicitGitHubToken</c> contract.</summary>
    public static JsonTypeInfo<LlmCopilotExplicitGitHubToken> LlmCopilotExplicitGitHubToken => Metadata<LlmCopilotExplicitGitHubToken>();
    /// <summary>JSON serializer metadata for the <c>LlmCopilotRuntimeAccount</c> contract.</summary>
    public static JsonTypeInfo<LlmCopilotRuntimeAccount> LlmCopilotRuntimeAccount => Metadata<LlmCopilotRuntimeAccount>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentRuntimeLoginScope</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentRuntimeLoginScope> LlmAgentRuntimeLoginScope => Metadata<LlmAgentRuntimeLoginScope>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentRuntimeAuthenticationSnapshot</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentRuntimeAuthenticationSnapshot> LlmAgentRuntimeAuthenticationSnapshot => Metadata<LlmAgentRuntimeAuthenticationSnapshot>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentRuntimeLoginOptions</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentRuntimeLoginOptions> LlmAgentRuntimeLoginOptions => Metadata<LlmAgentRuntimeLoginOptions>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentStagedLoginState</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentStagedLoginState> LlmAgentStagedLoginState => Metadata<LlmAgentStagedLoginState>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentRuntimeLoginResult</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentRuntimeLoginResult> LlmAgentRuntimeLoginResult => Metadata<LlmAgentRuntimeLoginResult>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentStagedStateDisposition</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentStagedStateDisposition> LlmAgentStagedStateDisposition => Metadata<LlmAgentStagedStateDisposition>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentStagedStateSnapshot</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentStagedStateSnapshot> LlmAgentStagedStateSnapshot => Metadata<LlmAgentStagedStateSnapshot>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentStagedStateCommitResult</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentStagedStateCommitResult> LlmAgentStagedStateCommitResult => Metadata<LlmAgentStagedStateCommitResult>();
    /// <summary>JSON serializer metadata for the <c>LlmCopilotCredentials</c> contract.</summary>
    public static JsonTypeInfo<LlmCopilotCredentials> LlmCopilotCredentials => new LlmCredentialJsonContext().LlmCopilotCredentials;
    /// <summary>JSON serializer metadata for the <c>LlmCopilotRuntimeConnectionCredential</c> contract.</summary>
    public static JsonTypeInfo<LlmCopilotRuntimeConnectionCredential> LlmCopilotRuntimeConnectionCredential => new LlmCredentialJsonContext().LlmCopilotRuntimeConnectionCredential;
    /// <summary>JSON serializer metadata for the <c>LlmFileCapabilities</c> contract.</summary>
    public static JsonTypeInfo<LlmFileCapabilities> LlmFileCapabilities => Metadata<LlmFileCapabilities>();
    /// <summary>JSON serializer metadata for the <c>LlmContextCacheCreate</c> contract.</summary>
    public static JsonTypeInfo<LlmContextCacheCreate> LlmContextCacheCreate => Metadata<LlmContextCacheCreate>();
    /// <summary>JSON serializer metadata for the <c>LlmContextCacheCreateCorrelation</c> contract.</summary>
    public static JsonTypeInfo<LlmContextCacheCreateCorrelation> LlmContextCacheCreateCorrelation => Metadata<LlmContextCacheCreateCorrelation>();
    /// <summary>JSON serializer metadata for the <c>LlmContextCache</c> contract.</summary>
    public static JsonTypeInfo<LlmContextCache> LlmContextCache => Metadata<LlmContextCache>();
    /// <summary>JSON serializer metadata for the <c>LlmContextCacheUsage</c> contract.</summary>
    public static JsonTypeInfo<LlmContextCacheUsage> LlmContextCacheUsage => Metadata<LlmContextCacheUsage>();
    /// <summary>JSON serializer metadata for the <c>LlmContextCacheList</c> contract.</summary>
    public static JsonTypeInfo<LlmContextCacheList> LlmContextCacheList => Metadata<LlmContextCacheList>();
    /// <summary>JSON serializer metadata for the <c>LlmContextCachePage</c> contract.</summary>
    public static JsonTypeInfo<LlmContextCachePage> LlmContextCachePage => Metadata<LlmContextCachePage>();
    /// <summary>JSON serializer metadata for the <c>LlmContextCacheDeleteResult</c> contract.</summary>
    public static JsonTypeInfo<LlmContextCacheDeleteResult> LlmContextCacheDeleteResult => Metadata<LlmContextCacheDeleteResult>();
    /// <summary>JSON serializer metadata for the <c>LlmContextCacheUnavailableReason</c> contract.</summary>
    public static JsonTypeInfo<LlmContextCacheUnavailableReason> LlmContextCacheUnavailableReason => Metadata<LlmContextCacheUnavailableReason>();
    /// <summary>JSON serializer metadata for the <c>LlmContextCacheMutation</c> contract.</summary>
    public static JsonTypeInfo<LlmContextCacheMutation> LlmContextCacheMutation => Metadata<LlmContextCacheMutation>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentShellRequest</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentShellRequest> LlmAgentShellRequest => Metadata<LlmAgentShellRequest>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPermissionChoiceKind</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPermissionChoiceKind> LlmAgentPermissionChoiceKind => Metadata<LlmAgentPermissionChoiceKind>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPermissionResult</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPermissionResult> LlmAgentPermissionResult => Metadata<LlmAgentPermissionResult>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentToolPresentationContent</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentToolPresentationContent> LlmAgentToolPresentationContent => Metadata<LlmAgentToolPresentationContent>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPresentedContentBlock</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPresentedContentBlock> LlmAgentPresentedContentBlock => Metadata<LlmAgentPresentedContentBlock>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPresentedResourceBody</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPresentedResourceBody> LlmAgentPresentedResourceBody => Metadata<LlmAgentPresentedResourceBody>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPermissionCancelled</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPermissionCancelled> LlmAgentPermissionCancelled => Metadata<LlmAgentPermissionCancelled>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentCallbackScope</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentCallbackScope> LlmAgentCallbackScope => Metadata<LlmAgentCallbackScope>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPermissionChoice</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPermissionChoice> LlmAgentPermissionChoice => Metadata<LlmAgentPermissionChoice>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPermissionRequest</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPermissionRequest> LlmAgentPermissionRequest => Metadata<LlmAgentPermissionRequest>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPermissionSelected</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPermissionSelected> LlmAgentPermissionSelected => Metadata<LlmAgentPermissionSelected>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentToolLocation</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentToolLocation> LlmAgentToolLocation => Metadata<LlmAgentToolLocation>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentToolPresentedContent</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentToolPresentedContent> LlmAgentToolPresentedContent => Metadata<LlmAgentToolPresentedContent>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentToolPresentedDiff</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentToolPresentedDiff> LlmAgentToolPresentedDiff => Metadata<LlmAgentToolPresentedDiff>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentToolPresentedTerminal</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentToolPresentedTerminal> LlmAgentToolPresentedTerminal => Metadata<LlmAgentToolPresentedTerminal>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentContentAnnotations</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentContentAnnotations> LlmAgentContentAnnotations => Metadata<LlmAgentContentAnnotations>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPresentedText</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPresentedText> LlmAgentPresentedText => Metadata<LlmAgentPresentedText>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPresentedImage</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPresentedImage> LlmAgentPresentedImage => Metadata<LlmAgentPresentedImage>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPresentedAudio</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPresentedAudio> LlmAgentPresentedAudio => Metadata<LlmAgentPresentedAudio>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPresentedResourceLink</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPresentedResourceLink> LlmAgentPresentedResourceLink => Metadata<LlmAgentPresentedResourceLink>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPresentedResourceText</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPresentedResourceText> LlmAgentPresentedResourceText => Metadata<LlmAgentPresentedResourceText>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPresentedResourceBytes</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPresentedResourceBytes> LlmAgentPresentedResourceBytes => Metadata<LlmAgentPresentedResourceBytes>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentPresentedEmbeddedResource</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentPresentedEmbeddedResource> LlmAgentPresentedEmbeddedResource => Metadata<LlmAgentPresentedEmbeddedResource>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentTextFileReadRequest</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentTextFileReadRequest> LlmAgentTextFileReadRequest => Metadata<LlmAgentTextFileReadRequest>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentTextFileWriteRequest</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentTextFileWriteRequest> LlmAgentTextFileWriteRequest => Metadata<LlmAgentTextFileWriteRequest>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentTextFileWriteResult</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentTextFileWriteResult> LlmAgentTextFileWriteResult => Metadata<LlmAgentTextFileWriteResult>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentEnvironmentVariable</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentEnvironmentVariable> LlmAgentEnvironmentVariable => Metadata<LlmAgentEnvironmentVariable>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentTerminalCreateRequest</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentTerminalCreateRequest> LlmAgentTerminalCreateRequest => Metadata<LlmAgentTerminalCreateRequest>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentTerminalExit</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentTerminalExit> LlmAgentTerminalExit => Metadata<LlmAgentTerminalExit>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentTerminalOutput</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentTerminalOutput> LlmAgentTerminalOutput => Metadata<LlmAgentTerminalOutput>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentToolPresentation</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentToolPresentation> LlmAgentToolPresentation => Metadata<LlmAgentToolPresentation>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentRuntimePolicy</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentRuntimePolicy> LlmAgentRuntimePolicy => Metadata<LlmAgentRuntimePolicy>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentHostRuntimePolicy</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentHostRuntimePolicy> LlmAgentHostRuntimePolicy => Metadata<LlmAgentHostRuntimePolicy>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentProviderRuntimePolicy</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentProviderRuntimePolicy> LlmAgentProviderRuntimePolicy => Metadata<LlmAgentProviderRuntimePolicy>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentRuntimePolicyKind</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentRuntimePolicyKind> LlmAgentRuntimePolicyKind => Metadata<LlmAgentRuntimePolicyKind>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentClientToolDefinition</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentClientToolDefinition> LlmAgentClientToolDefinition => Metadata<LlmAgentClientToolDefinition>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentToolDefinition</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentToolDefinition> LlmAgentToolDefinition => Metadata<LlmAgentToolDefinition>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentShellTool</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentShellTool> LlmAgentShellTool => Metadata<LlmAgentShellTool>();
    private static JsonTypeInfo<T> Metadata<T>() => CreateOptions().GetTypeInfo<T>();

    private static JsonSerializerOptions CreateOptions()
    {
        // Retain generated converters and compose Common's exact live-authority refusal policy.
        var options = new JsonSerializerOptions(ConnectLlmJsonContext.Default.Options) { TypeInfoResolver = new HostedTypeInfoResolver() };
        foreach (var type in new[] { typeof(IInferenceResourceAccess), typeof(IInferenceResourceReadHandle), typeof(IInferenceResourceWriteHandle), typeof(IProviderAuthorization), typeof(LlmHostedToolKind), typeof(LlmHostedToolPhase), typeof(LlmImageGenerationAction) })
        {
            options.Converters.Add(InferenceJson.InferenceResourceReference.Options.Converters.Single(converter => converter.CanConvert(type)));
        }
        options.Converters.Add(new CallerEnumConverter<LlmAgentPermissionChoiceKind>());
        options.Converters.Add(new CallerEnumConverter<LlmAgentContentObservationKind>());
        options.Converters.Add(new CallerEnumConverter<LlmAgentContentObservationMode>());
        options.Converters.Add(new CallerEnumConverter<LlmAgentRuntimePolicyKind>());
        options.Converters.Add(new CallerEnumConverter<LlmContextCacheUnavailableReason>());
        options.Converters.Add(new CallerEnumConverter<LlmContextCacheMutation>());
        options.Converters.Add(new CallerEnumConverter<LlmAzureApiMode>());
        options.Converters.Add(new CallerEnumConverter<LlmAzureAuthenticationMode>());
        options.Converters.Add(new CallerEnumConverter<LlmAzureCloud>());
        options.Converters.Add(new CallerEnumConverter<LlmAzureTokenAudience>());
        options.Converters.Add(new CallerEnumConverter<LlmCopilotEndpointMode>());
        options.Converters.Add(new CallerEnumConverter<LlmAcpAuthenticationFlow>());
        options.Converters.Add(new CallerEnumConverter<LlmAgentStagedStateDisposition>());
        options.Converters.Add(new FileRuntimeJsonConverter<LlmEmbeddingProviderService>("Live embedding facades are not portable data."));
        options.MakeReadOnly();
        return options;
    }

    private sealed class HostedTypeInfoResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type == typeof(LlmEmbeddingProviderService))
                return JsonMetadataServices.CreateValueInfo<LlmEmbeddingProviderService>(options,
                    new FileRuntimeJsonConverter<LlmEmbeddingProviderService>("Live embedding facades are not portable data."));
            if (LlmAgentCallerJson.RuntimeMetadata(type, options) is { } runtime) return runtime;
            if (LlmRuntimeAuthenticationJson.RuntimeMetadata(type, options) is { } authentication) return authentication;
            var metadata = ((IJsonTypeInfoResolver)ConnectLlmJsonContext.Default).GetTypeInfo(type, options);
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
            if (metadata is not null && (type == typeof(LlmHostedImageOutput) || type == typeof(LlmAgentPresentedImage)
                || type == typeof(LlmAgentPresentedAudio) || type == typeof(LlmAgentPresentedResourceBytes)))
                metadata.Properties.Single(property => property.Name == "Data").CustomConverter =
                    InferenceJson.LlmHostedImageOutput.Properties.Single(property => property.Name == "Data").CustomConverter;
            if (metadata is not null && (type == typeof(LlmAgentHostedToolCallStartedEvent) || type == typeof(LlmAgentHostedToolCallProgressEvent)
                || type == typeof(LlmAgentHostedToolCallCompletedEvent) || type == typeof(LlmAgentCitationEvent)
                || type == typeof(LlmAgentCitationsSnapshotEvent) || type == typeof(LlmAgentCompactedEvent)
                || type == typeof(LlmAgentImageOutputEvent)))
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
            if (metadata is not null && (type == typeof(LlmAgentToolPresentationEvent) || type == typeof(LlmAgentContentObservationEvent)))
            {
                metadata.OnSerializing = AgentObservationData.Validate;
                metadata.OnDeserialized = AgentObservationData.Validate;
            }
            return metadata;
        }
    }

    /// <summary>JSON serializer metadata for the <c>LlmInferenceRequest</c> contract.</summary>
    public static JsonTypeInfo<LlmInferenceRequest> PortableRequest => Metadata<LlmInferenceRequest>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentHostedToolCallStartedEvent</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentHostedToolCallStartedEvent> LlmAgentHostedToolCallStartedEvent => Metadata<LlmAgentHostedToolCallStartedEvent>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentHostedToolCallProgressEvent</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentHostedToolCallProgressEvent> LlmAgentHostedToolCallProgressEvent => Metadata<LlmAgentHostedToolCallProgressEvent>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentHostedToolCallCompletedEvent</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentHostedToolCallCompletedEvent> LlmAgentHostedToolCallCompletedEvent => Metadata<LlmAgentHostedToolCallCompletedEvent>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentCitationEvent</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentCitationEvent> LlmAgentCitationEvent => Metadata<LlmAgentCitationEvent>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentCitationsSnapshotEvent</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentCitationsSnapshotEvent> LlmAgentCitationsSnapshotEvent => Metadata<LlmAgentCitationsSnapshotEvent>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentCompactedEvent</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentCompactedEvent> LlmAgentCompactedEvent => Metadata<LlmAgentCompactedEvent>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentImageOutputEvent</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentImageOutputEvent> LlmAgentImageOutputEvent => Metadata<LlmAgentImageOutputEvent>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentResourceSelectionChangedEvent</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentResourceSelectionChangedEvent> LlmAgentResourceSelectionChangedEvent => Metadata<LlmAgentResourceSelectionChangedEvent>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentToolPresentationEvent</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentToolPresentationEvent> LlmAgentToolPresentationEvent => Metadata<LlmAgentToolPresentationEvent>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentContentObservationEvent</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentContentObservationEvent> LlmAgentContentObservationEvent => Metadata<LlmAgentContentObservationEvent>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentContentObservationKind</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentContentObservationKind> LlmAgentContentObservationKind => Metadata<LlmAgentContentObservationKind>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentContentObservationMode</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentContentObservationMode> LlmAgentContentObservationMode => Metadata<LlmAgentContentObservationMode>();
    /// <summary>JSON serializer metadata for the <c>LlmCredential</c> contract.</summary>
    public static JsonTypeInfo<LlmCredential> Credential => new LlmCredentialJsonContext().LlmCredential;
    /// <summary>JSON serializer metadata for the <c>LlmAccountUsageSnapshot</c> contract.</summary>
    public static JsonTypeInfo<LlmAccountUsageSnapshot> AccountUsageSnapshot => Metadata<LlmAccountUsageSnapshot>();
    /// <summary>JSON serializer metadata for the <c>LlmConnection</c> contract.</summary>
    public static JsonTypeInfo<LlmConnection> Connection => Metadata<LlmConnection>();
    /// <summary>JSON serializer metadata for the <c>LlmSetupConfiguration</c> contract.</summary>
    public static JsonTypeInfo<LlmSetupConfiguration> SetupConfiguration => Metadata<LlmSetupConfiguration>();
    /// <summary>JSON serializer metadata for the <c>LlmChatMessage</c> contract.</summary>
    public static JsonTypeInfo<LlmChatMessage> ChatMessage => Metadata<LlmChatMessage>();
    /// <summary>JSON serializer metadata for the <c>LlmTokenization</c> contract.</summary>
    public static JsonTypeInfo<LlmTokenization> Tokenization => Metadata<LlmTokenization>();
    /// <summary>JSON serializer metadata for the <c>LlmToken</c> contract.</summary>
    public static JsonTypeInfo<LlmToken> Token => Metadata<LlmToken>();
    /// <summary>JSON serializer metadata for the <c>LlmEmbeddingRequest</c> contract.</summary>
    public static JsonTypeInfo<LlmEmbeddingRequest> LlmEmbeddingRequest => Metadata<LlmEmbeddingRequest>();
    /// <summary>JSON serializer metadata for the <c>LlmEmbeddingResult</c> contract.</summary>
    public static JsonTypeInfo<LlmEmbeddingResult> LlmEmbeddingResult => Metadata<LlmEmbeddingResult>();
    /// <summary>JSON serializer metadata for the <c>LlmEmbeddingUsage</c> contract.</summary>
    public static JsonTypeInfo<LlmEmbeddingUsage> LlmEmbeddingUsage => Metadata<LlmEmbeddingUsage>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentContext</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentContext> AgentContext => Metadata<LlmAgentContext>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentContextEntry</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentContextEntry> AgentContextEntry => Metadata<LlmAgentContextEntry>();
    /// <summary>JSON serializer metadata for the <c>LlmAgentContextStatus</c> contract.</summary>
    public static JsonTypeInfo<LlmAgentContextStatus> AgentContextStatus => Metadata<LlmAgentContextStatus>();
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(LlmAgentContextJsonConverter),
        typeof(CallerAuthorityConverter<LlmAgentRuntimeLaunchInputs>),
        typeof(CallerAuthorityConverter<LlmAgentRuntimeLoginServices>),
        typeof(CallerAuthorityConverter<ILlmAgentRuntimeLoginTerminalFactory>),
        typeof(CallerAuthorityConverter<ILlmAgentRuntimeAuthentication>),
        typeof(CallerAuthorityConverter<LlmAgentRuntimeLoginTerminalRequest>),
        typeof(CallerAuthorityConverter<ILlmAuthPrompt>)])]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(LlmVertexCacheStaging))]
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
[JsonSerializable(typeof(LlmAgentTurnRequest))]
[JsonSerializable(typeof(LlmFileCapabilities))]
[JsonSerializable(typeof(LlmContextCacheCreate))]
[JsonSerializable(typeof(LlmContextCacheCreateCorrelation))]
[JsonSerializable(typeof(LlmContextCache))]
[JsonSerializable(typeof(LlmContextCacheUsage))]
[JsonSerializable(typeof(LlmContextCacheList))]
[JsonSerializable(typeof(LlmContextCachePage))]
[JsonSerializable(typeof(LlmContextCacheDeleteResult))]
[JsonSerializable(typeof(LlmContextCacheUnavailableReason))]
[JsonSerializable(typeof(LlmContextCacheMutation))]
[JsonSerializable(typeof(LlmAgentShellRequest))]
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
[JsonSerializable(typeof(LlmAccountUsageSnapshot))]
[JsonSerializable(typeof(LlmSetupConfiguration))]
[JsonSerializable(typeof(LlmInferenceRequest))]
[JsonSerializable(typeof(LlmHostedTool))]
[JsonSerializable(typeof(LlmAgentHostedToolCallStartedEvent))]
[JsonSerializable(typeof(LlmAgentHostedToolCallProgressEvent))]
[JsonSerializable(typeof(LlmAgentHostedToolCallCompletedEvent))]
[JsonSerializable(typeof(LlmAgentCitationEvent))]
[JsonSerializable(typeof(LlmAgentCitationsSnapshotEvent))]
[JsonSerializable(typeof(LlmAgentCompactedEvent))]
[JsonSerializable(typeof(LlmAgentImageOutputEvent))]
[JsonSerializable(typeof(LlmAgentResourceSelectionChangedEvent))]
[JsonSerializable(typeof(LlmAgentToolPresentationEvent))]
[JsonSerializable(typeof(LlmAgentContentObservationEvent))]
[JsonSerializable(typeof(LlmAgentContentObservationKind))]
[JsonSerializable(typeof(LlmAgentContentObservationMode))]
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
