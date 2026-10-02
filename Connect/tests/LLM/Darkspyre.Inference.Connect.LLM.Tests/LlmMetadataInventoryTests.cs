using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common;

namespace Darkspyre.Inference.Connect.LLM.Tests;

public sealed class LlmMetadataInventoryTests
{
    private const string CommonLlm = "Darkspyre.Inference.Common.LLM.";
    private const string CommonResources = "Darkspyre.Inference.Common.Resources.";
    private const string ConnectLlm = "Darkspyre.Inference.Connect.LLM.";

    private static Dictionary<string, string> CommonInventory()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Provenance"] = "Darkspyre.Inference.Common.InferenceProvenance",
            ["ProviderFileReference"] = "Darkspyre.Inference.Common.Media.ProviderFileReference",
            ["LlmRequest"] = CommonLlm + "LlmInferenceRequest",
            ["LlmResult"] = CommonLlm + "LlmInferenceResult",
            ["ContinuationState"] = CommonLlm + "LlmContinuationState",
            ["LlmModel"] = CommonLlm + "LlmModelInfo",
            ["ToolDefinition"] = CommonLlm + "LlmToolDefinition",
            ["ToolOutput"] = CommonLlm + "LlmToolOutput",
            ["ToolOutputPart"] = CommonLlm + "LlmToolOutputPart",
            ["TextDelta"] = CommonLlm + "LlmTextDeltaEvent",
            ["StructuredOutputSnapshot"] = CommonLlm + "LlmStructuredOutputSnapshotEvent",
            ["ReasoningDelta"] = CommonLlm + "LlmReasoningDeltaEvent",
            ["RefusalDelta"] = CommonLlm + "LlmRefusalDeltaEvent",
            ["Activity"] = CommonLlm + "LlmActivityEvent",
            ["ToolCallStarted"] = CommonLlm + "LlmToolCallStartedEvent",
            ["ToolCallArgumentsDelta"] = CommonLlm + "LlmToolCallArgumentsDeltaEvent",
            ["ToolCallCompleted"] = CommonLlm + "LlmToolCallCompletedEvent",
            ["UsageDelta"] = CommonLlm + "LlmUsageDeltaEvent",
            ["Completion"] = CommonLlm + "LlmCompletionEvent"
        };
        Add(expected, CommonLlm,
            "LlmMessage", "LlmCapabilities", "LlmProviderControls",
            "LlmHostedToolKind", "LlmHostedToolPhase", "LlmImageGenerationAction",
            "LlmHostedTool", "LlmWebSearchTool", "LlmCodeExecutionTool",
            "LlmImageGenerationTool", "LlmDocumentSearchTool", "LlmRemoteMcpTool",
            "LlmHostedToolOutput", "LlmHostedSearchSource", "LlmHostedSearchOutput",
            "LlmHostedCodeOutput", "LlmHostedDocumentMatch", "LlmHostedDocumentSearchOutput",
            "LlmHostedMcpOutput", "LlmHostedImageOutput", "LlmHostedToolCall",
            "LlmHostedToolCallStartedEvent", "LlmHostedToolCallProgressEvent", "LlmHostedToolCallCompletedEvent",
            "LlmCitation", "LlmCitationEvent", "LlmUsage",
            "LlmClientToolDefinition", "LlmShellSkill", "LlmShellTool",
            "LlmClientToolCall", "LlmToolCall", "LlmShellCall", "LlmClientToolResult",
            "LlmToolResult", "LlmShellCommandResult", "LlmShellCallResult",
            "LlmShellOutcome", "LlmShellExitedOutcome", "LlmShellTimedOutOutcome", "LlmShellCallEvent",
            "LlmMessageAttachment", "LlmAttachment", "LlmFileReference", "LlmResourceAttachment",
            "LlmToolOutputSessionResource", "LlmExplicitCacheReference");
        Add(expected, CommonResources,
            "InferenceResourceReference", "InferenceResourceDescriptor", "InferenceResourceCreate",
            "InferenceResourceSelectionVersion", "InferenceResourceSelection",
            "InferenceResourceCommitOptions", "InferenceResourceCommitResult",
            "InferenceResourceOperationPhase", "InferenceResourceCommitOutcome", "InferenceResourceOperationFailure");
        return expected;
    }

    private static Dictionary<string, string> ConnectInventory()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PortableRequest"] = CommonLlm + "LlmInferenceRequest",
            ["Credential"] = ConnectLlm + "LlmCredential",
            ["AccountUsageSnapshot"] = ConnectLlm + "LlmAccountUsageSnapshot",
            ["Connection"] = ConnectLlm + "LlmConnection",
            ["SetupConfiguration"] = ConnectLlm + "LlmSetupConfiguration",
            ["ChatRequest"] = ConnectLlm + "LlmChatRequest",
            ["ChatResult"] = ConnectLlm + "LlmChatResult",
            ["ChatMessage"] = ConnectLlm + "LlmChatMessage",
            ["Tokenization"] = ConnectLlm + "LlmTokenization",
            ["Token"] = ConnectLlm + "LlmToken",
            ["AgentContext"] = ConnectLlm + "LlmAgentContext",
            ["AgentContextEntry"] = ConnectLlm + "LlmAgentContextEntry",
            ["AgentContextStatus"] = ConnectLlm + "LlmAgentContextStatus",
            ["ServicePrincipalSecret"] = ConnectLlm + "LlmAzureIdentitySelection+ServicePrincipalSecret",
            ["ServicePrincipalCertificate"] = ConnectLlm + "LlmAzureIdentitySelection+ServicePrincipalCertificate",
            ["WorkloadIdentity"] = ConnectLlm + "LlmAzureIdentitySelection+WorkloadIdentity",
            ["SystemAssignedManagedIdentity"] = ConnectLlm + "LlmAzureIdentitySelection+SystemAssignedManagedIdentity",
            ["UserAssignedManagedIdentity"] = ConnectLlm + "LlmAzureIdentitySelection+UserAssignedManagedIdentity",
            ["ClientId"] = ConnectLlm + "LlmAzureManagedIdentityId+ClientId",
            ["ResourceId"] = ConnectLlm + "LlmAzureManagedIdentityId+ResourceId",
            ["ObjectId"] = ConnectLlm + "LlmAzureManagedIdentityId+ObjectId",
            ["ClientSecret"] = ConnectLlm + "LlmAzureSecret+ClientSecret",
            ["CertificatePkcs12"] = ConnectLlm + "LlmAzureSecret+CertificatePkcs12"
        };
        Add(expected, ConnectLlm,
            "XaiMultiAgentControls", "XaiXSearchTool", "LlmAgentCapabilities",
            "LlmAzureConfiguration", "LlmAzureApiMode", "LlmAzureAuthenticationMode",
            "LlmAzureCloud", "LlmAzureTokenAudience", "LlmAzureEntraConfiguration",
            "LlmAzureIdentitySelection", "LlmAzureManagedIdentityId", "LlmAzureSecret",
            "LlmAgentRuntimeProfile", "LlmAgentRuntimeStateConfiguration",
            "LlmAgentIsolatedRuntimeState", "LlmAgentAdoptedRuntimeState",
            "LlmAcpConfiguration", "LlmAcpToolRelayConfiguration",
            "LlmCopilotEndpointMode", "LlmCopilotConfiguration",
            "LlmAcpAuthenticationConfiguration", "LlmAcpExternalAuthentication",
            "LlmAcpAgentAuthentication", "LlmAcpTerminalAuthentication",
            "LlmAcpAuthenticationFlow", "LlmAcpAuthenticationMethod",
            "LlmCopilotAuthenticationConfiguration", "LlmCopilotExplicitGitHubToken", "LlmCopilotRuntimeAccount",
            "LlmAgentRuntimeLoginScope", "LlmAgentRuntimeAuthenticationSnapshot", "LlmAgentRuntimeLoginOptions",
            "LlmAgentStagedLoginState", "LlmAgentRuntimeLoginResult", "LlmAgentStagedStateDisposition",
            "LlmAgentStagedStateSnapshot", "LlmAgentStagedStateCommitResult",
            "LlmCopilotCredentials", "LlmCopilotRuntimeConnectionCredential",
            "XaiStoragePublicUrlRequest", "XaiStoragePublicUrlFlag", "XaiStoragePublicUrlConfiguration",
            "XaiFileStorageOptions", "XaiConnectionSelection",
            "LlmFileOrder", "LlmFileSort", "LlmFileContentFormat", "LlmProviderFile",
            "LlmFileUploadOptions", "LlmFileListRequest", "LlmFilePage", "LlmFileDeletion",
            "LlmFileDownloadOptions", "ResourceDownloadReceipt", "XaiPublicUrlOptions",
            "XaiPublicUrl", "XaiPublicUrlRevocation", "LlmFileCapabilities",
            "XaiSkill", "XaiSkillDeletion", "XaiSkillListRequest", "XaiSkillPage", "XaiSkillCapabilities",
            "LlmContextCacheCreate", "LlmContextCacheCreateCorrelation", "LlmContextCache",
            "LlmContextCacheUsage", "LlmContextCacheList", "LlmContextCachePage",
            "LlmContextCacheDeleteResult", "LlmContextCacheUnavailableReason", "LlmContextCacheMutation",
            "LlmAgentShellRequest", "LlmAgentCallerServiceKind", "LlmAgentPermissionChoiceKind",
            "LlmAgentPermissionResult", "LlmAgentPermissionCancelled", "LlmAgentPermissionSelected",
            "LlmAgentCallbackScope", "LlmAgentPermissionChoice", "LlmAgentPermissionRequest",
            "LlmAgentToolPresentation", "LlmAgentToolLocation", "LlmAgentToolPresentationContent",
            "LlmAgentToolPresentedContent", "LlmAgentToolPresentedDiff", "LlmAgentToolPresentedTerminal",
            "LlmAgentContentAnnotations", "LlmAgentPresentedContentBlock", "LlmAgentPresentedText",
            "LlmAgentPresentedImage", "LlmAgentPresentedAudio", "LlmAgentPresentedResourceLink",
            "LlmAgentPresentedResourceBody", "LlmAgentPresentedResourceText", "LlmAgentPresentedResourceBytes",
            "LlmAgentPresentedEmbeddedResource", "LlmAgentTextFileReadRequest", "LlmAgentTextFileWriteRequest",
            "LlmAgentTextFileWriteResult", "LlmAgentEnvironmentVariable", "LlmAgentTerminalCreateRequest",
            "LlmAgentTerminalExit", "LlmAgentTerminalOutput", "LlmAgentRuntimePolicy",
            "LlmAgentHostRuntimePolicy", "LlmAgentProviderRuntimePolicy", "LlmAgentRuntimePolicyKind",
            "LlmAgentClientToolDefinition", "LlmAgentToolDefinition", "LlmAgentShellTool",
            "LlmAgentHostedToolCallStartedEvent", "LlmAgentHostedToolCallProgressEvent",
            "LlmAgentHostedToolCallCompletedEvent", "LlmAgentCitationEvent", "LlmAgentCompactedEvent",
            "LlmAgentResourceSelectionChangedEvent", "LlmEmbeddingRequest", "LlmEmbeddingResult", "LlmEmbeddingUsage",
            "DocumentGroupAggregate", "DocumentMinK", "DocumentMaxK", "DocumentSearchGroupBy",
            "DocumentHybridReranker", "DocumentRerankerModel", "DocumentRrfReranker",
            "DocumentRetrievalMode", "DocumentHybridRetrieval", "DocumentSemanticRetrieval", "DocumentKeywordRetrieval",
            "XaiDocumentRankingMetric", "XaiDocumentRagPipeline", "XaiDocumentSearchOptions",
            "DocumentSearchRequest", "DocumentSearchMatch", "DocumentSearchResult");
        return expected;
    }

    private static void Add(Dictionary<string, string> expected, string prefix, params string[] names)
    {
        foreach (string name in names) expected.Add(name, prefix + name);
    }

    [Fact]
    public void ExactCommonLlmAndResourceExportInventoryPreservesNamesAndTypes() =>
        CheckInventory(typeof(InferenceJson), CommonInventory(), typeof(InferenceJson).Assembly, commonSubset: true);

    [Fact]
    public void ExactConnectExportInventoryPreservesNamesAndTypes() =>
        CheckInventory(typeof(ConnectLlmJson), ConnectInventory(), typeof(ConnectLlmJson).Assembly, commonSubset: false);

    [Fact]
    public void CompleteInventoriesExecuteInTheirActualForeignOwnersAndCollect()
    {
        WeakReference[] roots = ExerciseForeignInventory();
        for (int attempt = 0; roots.Any(root => root.IsAlive) && attempt < 20; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(10); }
        Assert.All(roots, root => Assert.False(root.IsAlive));
    }

    private static void CheckInventory(Type facade, Dictionary<string, string> expected, Assembly metadataOwner, bool commonSubset)
    {
        PropertyInfo[] properties = facade.GetProperties(BindingFlags.Public | BindingFlags.Static);
        if (commonSubset)
            properties = properties.Where(property =>
            {
                Type type = property.PropertyType.GenericTypeArguments.Single();
                return type.Namespace is "Darkspyre.Inference.Common.LLM" or "Darkspyre.Inference.Common.Resources"
                    || property.Name is "Provenance" or "ProviderFileReference";
            }).ToArray();
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), properties.Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (PropertyInfo property in properties)
        {
            Assert.True(property.PropertyType.IsGenericType);
            Assert.Equal(typeof(JsonTypeInfo<>), property.PropertyType.GetGenericTypeDefinition());
            Assert.Null(property.SetMethod);
            var metadata = Assert.IsAssignableFrom<JsonTypeInfo>(property.GetValue(null));
            Assert.Equal(expected[property.Name], metadata.Type.FullName);
            Assert.Equal(metadata.Type, property.PropertyType.GenericTypeArguments.Single());
            Assert.Same(metadataOwner, metadata.Options.TypeInfoResolver!.GetType().Assembly);
            Assert.True(metadata.IsReadOnly);
            Assert.True(metadata.Options.IsReadOnly);
            Assert.Same(AssemblyLoadContext.GetLoadContext(metadataOwner), AssemblyLoadContext.GetLoadContext(metadata.Type.Assembly));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseForeignInventory()
    {
        var generation = new AssemblyLoadContext("llm-metadata-inventory", isCollectible: true);
        Assembly common = generation.LoadFromAssemblyPath(typeof(InferenceJson).Assembly.Location);
        Assembly connect = generation.LoadFromAssemblyPath(typeof(ConnectLlmJson).Assembly.Location);
        WeakReference[] roots = [new(generation), new(common), new(connect)];
        try
        {
            foreach ((Assembly source, Assembly actual) in new[]
                { (typeof(InferenceJson).Assembly, common), (typeof(ConnectLlmJson).Assembly, connect) })
            {
                Assert.NotSame(source, actual);
                Assert.Equal(source.ManifestModule.ModuleVersionId, actual.ManifestModule.ModuleVersionId);
                Assert.Same(generation, AssemblyLoadContext.GetLoadContext(actual));
            }
            CheckInventory(common.GetType(typeof(InferenceJson).FullName!, throwOnError: true)!, CommonInventory(), common, commonSubset: true);
            CheckInventory(connect.GetType(typeof(ConnectLlmJson).FullName!, throwOnError: true)!, ConnectInventory(), connect, commonSubset: false);
        }
        finally { generation.Unload(); }
        return roots;
    }
}
