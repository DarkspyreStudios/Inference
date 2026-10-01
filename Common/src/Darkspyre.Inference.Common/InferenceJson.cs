using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.Video;

namespace Darkspyre.Inference.Common;

/// <summary>Common-owned metadata for closed data contracts. No reflection fallback is installed.</summary>
public static class InferenceJson
{
    public static JsonTypeInfo<VideoProviderOptions> VideoProviderOptions => Metadata<VideoProviderOptions>();
    public static JsonTypeInfo<VideoRequest> VideoRequest => Metadata<VideoRequest>();
    public static JsonTypeInfo<VideoGenerationRequest> VideoGenerationRequest => Metadata<VideoGenerationRequest>();
    public static JsonTypeInfo<VideoEditRequest> VideoEditRequest => Metadata<VideoEditRequest>();
    public static JsonTypeInfo<VideoExtensionRequest> VideoExtensionRequest => Metadata<VideoExtensionRequest>();
    public static JsonTypeInfo<VideoKeyframe> VideoKeyframe => Metadata<VideoKeyframe>();
    public static JsonTypeInfo<VideoReferenceAudio> VideoReferenceAudio => Metadata<VideoReferenceAudio>();
    public static JsonTypeInfo<VideoVoiceReference> VideoVoiceReference => Metadata<VideoVoiceReference>();
    public static JsonTypeInfo<VideoAudioReference> VideoAudioReference => Metadata<VideoAudioReference>();
    public static JsonTypeInfo<VideoJobState> VideoJobState => Metadata<VideoJobState>();
    public static JsonTypeInfo<VideoOperation> VideoOperation => Metadata<VideoOperation>();
    public static JsonTypeInfo<VideoJob> VideoJob => Metadata<VideoJob>();
    public static JsonTypeInfo<VideoOutput> VideoOutput => Metadata<VideoOutput>();
    public static JsonTypeInfo<VideoJobSnapshot> VideoJobSnapshot => Metadata<VideoJobSnapshot>();
    public static JsonTypeInfo<VideoCapabilities> VideoCapabilities => Metadata<VideoCapabilities>();
    public static JsonTypeInfo<LlmExplicitCacheReference> LlmExplicitCacheReference => Metadata<LlmExplicitCacheReference>();
    public static JsonTypeInfo<LlmClientToolDefinition> LlmClientToolDefinition => Metadata<LlmClientToolDefinition>();
    public static JsonTypeInfo<LlmShellSkill> LlmShellSkill => Metadata<LlmShellSkill>();
    public static JsonTypeInfo<LlmShellTool> LlmShellTool => Metadata<LlmShellTool>();
    public static JsonTypeInfo<LlmClientToolCall> LlmClientToolCall => Metadata<LlmClientToolCall>();
    public static JsonTypeInfo<LlmToolCall> LlmToolCall => Metadata<LlmToolCall>();
    public static JsonTypeInfo<LlmShellCall> LlmShellCall => Metadata<LlmShellCall>();
    public static JsonTypeInfo<LlmClientToolResult> LlmClientToolResult => Metadata<LlmClientToolResult>();
    public static JsonTypeInfo<LlmToolResult> LlmToolResult => Metadata<LlmToolResult>();
    public static JsonTypeInfo<LlmShellCommandResult> LlmShellCommandResult => Metadata<LlmShellCommandResult>();
    public static JsonTypeInfo<LlmShellCallResult> LlmShellCallResult => Metadata<LlmShellCallResult>();
    public static JsonTypeInfo<LlmShellOutcome> LlmShellOutcome => Metadata<LlmShellOutcome>();
    public static JsonTypeInfo<LlmShellExitedOutcome> LlmShellExitedOutcome => Metadata<LlmShellExitedOutcome>();
    public static JsonTypeInfo<LlmShellTimedOutOutcome> LlmShellTimedOutOutcome => Metadata<LlmShellTimedOutOutcome>();
    public static JsonTypeInfo<LlmShellCallEvent> LlmShellCallEvent => Metadata<LlmShellCallEvent>();
    private static readonly JsonSerializerOptions OwnedOptions = CreateOptions();

    private static JsonTypeInfo<T> Metadata<T>() => OwnedOptions.GetTypeInfo<T>();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(InferenceJsonContext.Default.Options) { TypeInfoResolver = new HostedTypeInfoResolver() };
        options.Converters.Add(new VideoJobStateJsonConverter());
        options.Converters.Add(new VideoOperationJsonConverter());
        options.Converters.Add(new VideoProviderOptionsJsonConverter());
        options.Converters.Add(new VideoInferenceJsonConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed class HostedTypeInfoResolver : IJsonTypeInfoResolver
    {
        private readonly HostedImageDataJsonConverter imageData = new((JsonConverter<ReadOnlyMemory<byte>>)InferenceJsonContext.Default.Options.GetConverter(typeof(ReadOnlyMemory<byte>)));

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            var metadata = ((IJsonTypeInfoResolver)InferenceJsonContext.Default).GetTypeInfo(type, options);
            if ((type == typeof(LlmHostedImageOutput) || type == typeof(BinaryMediaSource)) && metadata is not null)
                metadata.Properties.Single(property => property.Name == "Data").CustomConverter = imageData;
            return metadata;
        }
    }

    public static JsonTypeInfo<LlmMessageAttachment> LlmMessageAttachment => Metadata<LlmMessageAttachment>();
    public static JsonTypeInfo<LlmAttachment> LlmAttachment => Metadata<LlmAttachment>();
    public static JsonTypeInfo<LlmFileReference> LlmFileReference => Metadata<LlmFileReference>();
    public static JsonTypeInfo<LlmResourceAttachment> LlmResourceAttachment => Metadata<LlmResourceAttachment>();
    public static JsonTypeInfo<LlmToolOutputSessionResource> LlmToolOutputSessionResource => Metadata<LlmToolOutputSessionResource>();
    public static JsonTypeInfo<InferenceMediaSourceKind> InferenceMediaSourceKind => Metadata<InferenceMediaSourceKind>();
    public static JsonTypeInfo<InferenceMediaSource> InferenceMediaSource => Metadata<InferenceMediaSource>();
    public static JsonTypeInfo<BinaryMediaSource> BinaryMediaSource => Metadata<BinaryMediaSource>();
    public static JsonTypeInfo<UrlMediaSource> UrlMediaSource => Metadata<UrlMediaSource>();
    public static JsonTypeInfo<ProviderFileReference> ProviderFileReference => Metadata<ProviderFileReference>();
    public static JsonTypeInfo<FileMediaSource> FileMediaSource => Metadata<FileMediaSource>();
    public static JsonTypeInfo<ResourceMediaSource> ResourceMediaSource => Metadata<ResourceMediaSource>();
    public static JsonTypeInfo<InferenceResourceScope> InferenceResourceScope => Metadata<InferenceResourceScope>();
    public static JsonTypeInfo<InferenceResourceFailure> InferenceResourceFailure => Metadata<InferenceResourceFailure>();
    public static JsonTypeInfo<InferenceProvenance> Provenance => Metadata<InferenceProvenance>();
    public static JsonTypeInfo<LlmInferenceRequest> LlmRequest => Metadata<LlmInferenceRequest>();
    public static JsonTypeInfo<LlmInferenceResult> LlmResult => Metadata<LlmInferenceResult>();
    public static JsonTypeInfo<LlmMessage> LlmMessage => Metadata<LlmMessage>();
    public static JsonTypeInfo<LlmContinuationState> ContinuationState => Metadata<LlmContinuationState>();
    public static JsonTypeInfo<LlmCapabilities> LlmCapabilities => Metadata<LlmCapabilities>();
    public static JsonTypeInfo<LlmModelInfo> LlmModel => Metadata<LlmModelInfo>();
    public static JsonTypeInfo<LlmToolDefinition> ToolDefinition => Metadata<LlmToolDefinition>();
    public static JsonTypeInfo<LlmToolOutput> ToolOutput => Metadata<LlmToolOutput>();
    public static JsonTypeInfo<LlmToolOutputPart> ToolOutputPart => Metadata<LlmToolOutputPart>();
    public static JsonTypeInfo<LlmTextDeltaEvent> TextDelta => Metadata<LlmTextDeltaEvent>();
    public static JsonTypeInfo<LlmStructuredOutputSnapshotEvent> StructuredOutputSnapshot => Metadata<LlmStructuredOutputSnapshotEvent>();
    public static JsonTypeInfo<LlmReasoningDeltaEvent> ReasoningDelta => Metadata<LlmReasoningDeltaEvent>();
    public static JsonTypeInfo<LlmRefusalDeltaEvent> RefusalDelta => Metadata<LlmRefusalDeltaEvent>();
    public static JsonTypeInfo<LlmActivityEvent> Activity => Metadata<LlmActivityEvent>();
    public static JsonTypeInfo<LlmToolCallStartedEvent> ToolCallStarted => Metadata<LlmToolCallStartedEvent>();
    public static JsonTypeInfo<LlmToolCallArgumentsDeltaEvent> ToolCallArgumentsDelta => Metadata<LlmToolCallArgumentsDeltaEvent>();
    public static JsonTypeInfo<LlmToolCallCompletedEvent> ToolCallCompleted => Metadata<LlmToolCallCompletedEvent>();
    public static JsonTypeInfo<LlmUsageDeltaEvent> UsageDelta => Metadata<LlmUsageDeltaEvent>();
    public static JsonTypeInfo<LlmUsage> LlmUsage => Metadata<LlmUsage>();
    public static JsonTypeInfo<LlmHostedToolKind> LlmHostedToolKind => Metadata<LlmHostedToolKind>();
    public static JsonTypeInfo<LlmHostedToolPhase> LlmHostedToolPhase => Metadata<LlmHostedToolPhase>();
    public static JsonTypeInfo<LlmImageGenerationAction> LlmImageGenerationAction => Metadata<LlmImageGenerationAction>();
    public static JsonTypeInfo<LlmHostedTool> LlmHostedTool => Metadata<LlmHostedTool>();
    public static JsonTypeInfo<LlmWebSearchTool> LlmWebSearchTool => Metadata<LlmWebSearchTool>();
    public static JsonTypeInfo<LlmCodeExecutionTool> LlmCodeExecutionTool => Metadata<LlmCodeExecutionTool>();
    public static JsonTypeInfo<LlmImageGenerationTool> LlmImageGenerationTool => Metadata<LlmImageGenerationTool>();
    public static JsonTypeInfo<LlmDocumentSearchTool> LlmDocumentSearchTool => Metadata<LlmDocumentSearchTool>();
    public static JsonTypeInfo<LlmRemoteMcpTool> LlmRemoteMcpTool => Metadata<LlmRemoteMcpTool>();
    public static JsonTypeInfo<LlmHostedToolOutput> LlmHostedToolOutput => Metadata<LlmHostedToolOutput>();
    public static JsonTypeInfo<LlmHostedSearchSource> LlmHostedSearchSource => Metadata<LlmHostedSearchSource>();
    public static JsonTypeInfo<LlmHostedSearchOutput> LlmHostedSearchOutput => Metadata<LlmHostedSearchOutput>();
    public static JsonTypeInfo<LlmHostedCodeOutput> LlmHostedCodeOutput => Metadata<LlmHostedCodeOutput>();
    public static JsonTypeInfo<LlmHostedDocumentMatch> LlmHostedDocumentMatch => Metadata<LlmHostedDocumentMatch>();
    public static JsonTypeInfo<LlmHostedDocumentSearchOutput> LlmHostedDocumentSearchOutput => Metadata<LlmHostedDocumentSearchOutput>();
    public static JsonTypeInfo<LlmHostedMcpOutput> LlmHostedMcpOutput => Metadata<LlmHostedMcpOutput>();
    public static JsonTypeInfo<LlmHostedImageOutput> LlmHostedImageOutput => Metadata<LlmHostedImageOutput>();
    public static JsonTypeInfo<LlmHostedToolCall> LlmHostedToolCall => Metadata<LlmHostedToolCall>();
    public static JsonTypeInfo<LlmHostedToolCallStartedEvent> LlmHostedToolCallStartedEvent => Metadata<LlmHostedToolCallStartedEvent>();
    public static JsonTypeInfo<LlmHostedToolCallProgressEvent> LlmHostedToolCallProgressEvent => Metadata<LlmHostedToolCallProgressEvent>();
    public static JsonTypeInfo<LlmHostedToolCallCompletedEvent> LlmHostedToolCallCompletedEvent => Metadata<LlmHostedToolCallCompletedEvent>();
    public static JsonTypeInfo<LlmCitation> LlmCitation => Metadata<LlmCitation>();
    public static JsonTypeInfo<LlmCitationEvent> LlmCitationEvent => Metadata<LlmCitationEvent>();
    public static JsonTypeInfo<LlmCompletionEvent> Completion => Metadata<LlmCompletionEvent>();
    public static JsonTypeInfo<DecisionQuestion> DecisionQuestion => Metadata<DecisionQuestion>();
    public static JsonTypeInfo<DecisionAnswer> DecisionAnswer => Metadata<DecisionAnswer>();
    public static JsonTypeInfo<DecisionInferenceRequest> DecisionRequest => Metadata<DecisionInferenceRequest>();
    public static JsonTypeInfo<DecisionInferenceResult> DecisionResult => Metadata<DecisionInferenceResult>();
    public static JsonTypeInfo<DecisionCapabilities> DecisionCapabilities => Metadata<DecisionCapabilities>();
    public static JsonTypeInfo<DecisionInputMeasurement> DecisionMeasurement => Metadata<DecisionInputMeasurement>();
    public static JsonTypeInfo<ImageGenerationRequest> ImageRequest => Metadata<ImageGenerationRequest>();
    public static JsonTypeInfo<ImageGenerationResult> ImageResult => Metadata<ImageGenerationResult>();
    public static JsonTypeInfo<ImageGenerationProgress> ImageProgress => Metadata<ImageGenerationProgress>();
    public static JsonTypeInfo<SetupStatus> SetupStatus => Metadata<SetupStatus>();
    public static JsonTypeInfo<SetupValidation> SetupValidation => Metadata<SetupValidation>();
    public static JsonTypeInfo<SetupConfigurationIdentity> SetupIdentity => Metadata<SetupConfigurationIdentity>();
    public static JsonTypeInfo<SetupCommitResult> SetupCommit => Metadata<SetupCommitResult>();
    public static JsonTypeInfo<SetupEditResult> SetupEdit => Metadata<SetupEditResult>();
    public static JsonTypeInfo<SetupActionDescriptor> SetupAction => Metadata<SetupActionDescriptor>();
    public static JsonTypeInfo<SetupProgress> SetupProgress => Metadata<SetupProgress>();
    public static JsonTypeInfo<SetupChoicePage> SetupChoices => Metadata<SetupChoicePage>();
    public static JsonTypeInfo<InferenceResourceReference> InferenceResourceReference => Metadata<InferenceResourceReference>();
    public static JsonTypeInfo<InferenceResourceDescriptor> InferenceResourceDescriptor => Metadata<InferenceResourceDescriptor>();
    public static JsonTypeInfo<InferenceResourceCreate> InferenceResourceCreate => Metadata<InferenceResourceCreate>();
    public static JsonTypeInfo<InferenceResourceSelectionVersion> InferenceResourceSelectionVersion => Metadata<InferenceResourceSelectionVersion>();
    public static JsonTypeInfo<InferenceResourceSelection> InferenceResourceSelection => Metadata<InferenceResourceSelection>();
    public static JsonTypeInfo<InferenceResourceCommitOptions> InferenceResourceCommitOptions => Metadata<InferenceResourceCommitOptions>();
    public static JsonTypeInfo<InferenceResourceCommitResult> InferenceResourceCommitResult => Metadata<InferenceResourceCommitResult>();
    public static JsonTypeInfo<InferenceResourceOperationPhase> InferenceResourceOperationPhase => Metadata<InferenceResourceOperationPhase>();
    public static JsonTypeInfo<InferenceResourceCommitOutcome> InferenceResourceCommitOutcome => Metadata<InferenceResourceCommitOutcome>();
    public static JsonTypeInfo<InferenceResourceOperationFailure> InferenceResourceOperationFailure => Metadata<InferenceResourceOperationFailure>();
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(ResourceAccessJsonConverter), typeof(ResourceReadHandleJsonConverter), typeof(ResourceWriteHandleJsonConverter), typeof(ProviderAuthorizationJsonConverter), typeof(HostedToolPhaseJsonConverter), typeof(ImageGenerationActionJsonConverter)])]
[JsonSerializable(typeof(InferenceProvenance))]
[JsonSerializable(typeof(VideoProviderOptions))]
[JsonSerializable(typeof(VideoRequest))]
[JsonSerializable(typeof(VideoGenerationRequest))]
[JsonSerializable(typeof(VideoEditRequest))]
[JsonSerializable(typeof(VideoExtensionRequest))]
[JsonSerializable(typeof(VideoKeyframe))]
[JsonSerializable(typeof(VideoReferenceAudio))]
[JsonSerializable(typeof(VideoVoiceReference))]
[JsonSerializable(typeof(VideoAudioReference))]
[JsonSerializable(typeof(VideoJobState))]
[JsonSerializable(typeof(VideoOperation))]
[JsonSerializable(typeof(VideoJob))]
[JsonSerializable(typeof(VideoOutput))]
[JsonSerializable(typeof(VideoJobSnapshot))]
[JsonSerializable(typeof(VideoCapabilities))]
[JsonSerializable(typeof(InferenceMediaSourceKind))]
[JsonSerializable(typeof(InferenceMediaSource))]
[JsonSerializable(typeof(BinaryMediaSource))]
[JsonSerializable(typeof(UrlMediaSource))]
[JsonSerializable(typeof(ProviderFileReference))]
[JsonSerializable(typeof(FileMediaSource))]
[JsonSerializable(typeof(ResourceMediaSource))]
[JsonSerializable(typeof(InferenceResourceScope))]
[JsonSerializable(typeof(InferenceResourceFailure))]
[JsonSerializable(typeof(LlmInferenceRequest))]
[JsonSerializable(typeof(LlmInferenceResult))]
[JsonSerializable(typeof(LlmMessage))]
[JsonSerializable(typeof(LlmMessageAttachment))]
[JsonSerializable(typeof(LlmAttachment))]
[JsonSerializable(typeof(LlmFileReference))]
[JsonSerializable(typeof(LlmResourceAttachment))]
[JsonSerializable(typeof(LlmToolOutputSessionResource))]
[JsonSerializable(typeof(LlmContinuationState))]
[JsonSerializable(typeof(LlmCapabilities))]
[JsonSerializable(typeof(LlmExplicitCacheReference))]
[JsonSerializable(typeof(LlmModelInfo))]
[JsonSerializable(typeof(LlmToolDefinition))]
[JsonSerializable(typeof(LlmClientToolDefinition))]
[JsonSerializable(typeof(LlmShellSkill))]
[JsonSerializable(typeof(LlmShellTool))]
[JsonSerializable(typeof(LlmClientToolCall))]
[JsonSerializable(typeof(LlmToolCall))]
[JsonSerializable(typeof(LlmShellCall))]
[JsonSerializable(typeof(LlmClientToolResult))]
[JsonSerializable(typeof(LlmToolResult))]
[JsonSerializable(typeof(LlmShellCommandResult))]
[JsonSerializable(typeof(LlmShellCallResult))]
[JsonSerializable(typeof(LlmShellOutcome))]
[JsonSerializable(typeof(LlmShellExitedOutcome))]
[JsonSerializable(typeof(LlmShellTimedOutOutcome))]
[JsonSerializable(typeof(LlmShellCallEvent))]
[JsonSerializable(typeof(LlmToolOutput))]
[JsonSerializable(typeof(LlmToolOutputPart))]
[JsonSerializable(typeof(LlmTextDeltaEvent))]
[JsonSerializable(typeof(LlmStructuredOutputSnapshotEvent))]
[JsonSerializable(typeof(LlmReasoningDeltaEvent))]
[JsonSerializable(typeof(LlmRefusalDeltaEvent))]
[JsonSerializable(typeof(LlmActivityEvent))]
[JsonSerializable(typeof(LlmToolCallStartedEvent))]
[JsonSerializable(typeof(LlmToolCallArgumentsDeltaEvent))]
[JsonSerializable(typeof(LlmToolCallCompletedEvent))]
[JsonSerializable(typeof(LlmUsageDeltaEvent))]
[JsonSerializable(typeof(LlmUsage))]
[JsonSerializable(typeof(LlmHostedToolKind))]
[JsonSerializable(typeof(LlmHostedToolPhase))]
[JsonSerializable(typeof(LlmImageGenerationAction))]
[JsonSerializable(typeof(LlmHostedTool))]
[JsonSerializable(typeof(LlmWebSearchTool))]
[JsonSerializable(typeof(LlmCodeExecutionTool))]
[JsonSerializable(typeof(LlmImageGenerationTool))]
[JsonSerializable(typeof(LlmDocumentSearchTool))]
[JsonSerializable(typeof(LlmRemoteMcpTool))]
[JsonSerializable(typeof(LlmHostedToolOutput))]
[JsonSerializable(typeof(LlmHostedSearchSource))]
[JsonSerializable(typeof(LlmHostedSearchOutput))]
[JsonSerializable(typeof(LlmHostedCodeOutput))]
[JsonSerializable(typeof(LlmHostedDocumentMatch))]
[JsonSerializable(typeof(LlmHostedDocumentSearchOutput))]
[JsonSerializable(typeof(LlmHostedMcpOutput))]
[JsonSerializable(typeof(LlmHostedImageOutput))]
[JsonSerializable(typeof(LlmHostedToolCall))]
[JsonSerializable(typeof(LlmHostedToolCallStartedEvent))]
[JsonSerializable(typeof(LlmHostedToolCallProgressEvent))]
[JsonSerializable(typeof(LlmHostedToolCallCompletedEvent))]
[JsonSerializable(typeof(LlmCitation))]
[JsonSerializable(typeof(LlmCitationEvent))]
[JsonSerializable(typeof(LlmCompletionEvent))]
[JsonSerializable(typeof(DecisionQuestion))]
[JsonSerializable(typeof(DecisionAnswer))]
[JsonSerializable(typeof(DecisionInferenceRequest))]
[JsonSerializable(typeof(DecisionInferenceResult))]
[JsonSerializable(typeof(DecisionCapabilities))]
[JsonSerializable(typeof(DecisionInputMeasurement))]
[JsonSerializable(typeof(ImageGenerationRequest))]
[JsonSerializable(typeof(ImageGenerationResult))]
[JsonSerializable(typeof(ImageGenerationProgress))]
[JsonSerializable(typeof(SetupStatus))]
[JsonSerializable(typeof(SetupValidation))]
[JsonSerializable(typeof(SetupConfigurationIdentity))]
[JsonSerializable(typeof(SetupCommitResult))]
[JsonSerializable(typeof(SetupEditResult))]
[JsonSerializable(typeof(SetupActionDescriptor))]
[JsonSerializable(typeof(SetupProgress))]
[JsonSerializable(typeof(SetupChoicePage))]
[JsonSerializable(typeof(InferenceResourceReference))]
[JsonSerializable(typeof(InferenceResourceDescriptor))]
[JsonSerializable(typeof(InferenceResourceCreate))]
[JsonSerializable(typeof(InferenceResourceSelectionVersion))]
[JsonSerializable(typeof(InferenceResourceSelection))]
[JsonSerializable(typeof(InferenceResourceCommitOptions))]
[JsonSerializable(typeof(InferenceResourceCommitResult))]
[JsonSerializable(typeof(InferenceResourceOperationPhase))]
[JsonSerializable(typeof(InferenceResourceCommitOutcome))]
[JsonSerializable(typeof(InferenceResourceOperationFailure))]
internal sealed partial class InferenceJsonContext : JsonSerializerContext;
