using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Common.Embedding;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Media;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Realtime;
using Darkspyre.Inference.Common.Setup;
using Darkspyre.Inference.Common.STT;
using Darkspyre.Inference.Common.Video;

namespace Darkspyre.Inference.Common;

/// <summary>Common-owned metadata for closed data contracts. No reflection fallback is installed.</summary>
public static class InferenceJson
{
    /// <summary>JSON metadata for <see cref="LlmProviderControls"/>.</summary>
    public static JsonTypeInfo<LlmProviderControls> LlmProviderControls => Metadata<LlmProviderControls>();
    /// <summary>JSON metadata for <see cref="SpeechAudioEncoding"/>.</summary>
    public static JsonTypeInfo<SpeechAudioEncoding> SpeechAudioEncoding => Metadata<SpeechAudioEncoding>();
    /// <summary>JSON metadata for <see cref="SpeechTranscriptionInput"/>.</summary>
    public static JsonTypeInfo<SpeechTranscriptionInput> SpeechTranscriptionInput => Metadata<SpeechTranscriptionInput>();
    /// <summary>JSON metadata for <see cref="BinarySpeechInput"/>.</summary>
    public static JsonTypeInfo<BinarySpeechInput> BinarySpeechInput => Metadata<BinarySpeechInput>();
    /// <summary>JSON metadata for <see cref="UrlSpeechInput"/>.</summary>
    public static JsonTypeInfo<UrlSpeechInput> UrlSpeechInput => Metadata<UrlSpeechInput>();
    /// <summary>JSON metadata for <see cref="ResourceSpeechInput"/>.</summary>
    public static JsonTypeInfo<ResourceSpeechInput> ResourceSpeechInput => Metadata<ResourceSpeechInput>();
    /// <summary>JSON metadata for <see cref="SpeechTranscriptionOptions"/>.</summary>
    public static JsonTypeInfo<SpeechTranscriptionOptions> SpeechTranscriptionOptions => Metadata<SpeechTranscriptionOptions>();
    /// <summary>JSON metadata for <see cref="SpeechStreamOptions"/>.</summary>
    public static JsonTypeInfo<SpeechStreamOptions> SpeechStreamOptions => Metadata<SpeechStreamOptions>();
    /// <summary>JSON metadata for <see cref="SpeechWord"/>.</summary>
    public static JsonTypeInfo<SpeechWord> SpeechWord => Metadata<SpeechWord>();
    /// <summary>JSON metadata for <see cref="SpeechChannelTranscript"/>.</summary>
    public static JsonTypeInfo<SpeechChannelTranscript> SpeechChannelTranscript => Metadata<SpeechChannelTranscript>();
    /// <summary>JSON metadata for <see cref="SpeechTranscriptionResult"/>.</summary>
    public static JsonTypeInfo<SpeechTranscriptionResult> SpeechTranscriptionResult => Metadata<SpeechTranscriptionResult>();
    /// <summary>JSON metadata for <see cref="SpeechTranscriptCreatedEvent"/>.</summary>
    public static JsonTypeInfo<SpeechTranscriptCreatedEvent> SpeechTranscriptCreatedEvent => Metadata<SpeechTranscriptCreatedEvent>();
    /// <summary>JSON metadata for <see cref="SpeechTranscriptPartialEvent"/>.</summary>
    public static JsonTypeInfo<SpeechTranscriptPartialEvent> SpeechTranscriptPartialEvent => Metadata<SpeechTranscriptPartialEvent>();
    /// <summary>JSON metadata for <see cref="SpeechTranscriptDoneEvent"/>.</summary>
    public static JsonTypeInfo<SpeechTranscriptDoneEvent> SpeechTranscriptDoneEvent => Metadata<SpeechTranscriptDoneEvent>();
    /// <summary>JSON metadata for <see cref="SpeechTranscriptErrorEvent"/>.</summary>
    public static JsonTypeInfo<SpeechTranscriptErrorEvent> SpeechTranscriptErrorEvent => Metadata<SpeechTranscriptErrorEvent>();
    /// <summary>JSON metadata for <see cref="SpeechTranscriptionCapabilities"/>.</summary>
    public static JsonTypeInfo<SpeechTranscriptionCapabilities> SpeechTranscriptionCapabilities => Metadata<SpeechTranscriptionCapabilities>();
    /// <summary>JSON metadata for <see cref="SpeechTranscriptionModeCapabilities"/>.</summary>
    public static JsonTypeInfo<SpeechTranscriptionModeCapabilities> SpeechTranscriptionModeCapabilities => Metadata<SpeechTranscriptionModeCapabilities>();
    /// <summary>JSON metadata for <see cref="RealtimeAudioEncoding"/>.</summary>
    public static JsonTypeInfo<RealtimeAudioEncoding> RealtimeAudioEncoding => Metadata<RealtimeAudioEncoding>();
    /// <summary>JSON metadata for <see cref="RealtimeAudioTransport"/>.</summary>
    public static JsonTypeInfo<RealtimeAudioTransport> RealtimeAudioTransport => Metadata<RealtimeAudioTransport>();
    /// <summary>JSON metadata for <see cref="RealtimeModality"/>.</summary>
    public static JsonTypeInfo<RealtimeModality> RealtimeModality => Metadata<RealtimeModality>();
    /// <summary>JSON metadata for <see cref="RealtimeMessageRole"/>.</summary>
    public static JsonTypeInfo<RealtimeMessageRole> RealtimeMessageRole => Metadata<RealtimeMessageRole>();
    /// <summary>JSON metadata for <see cref="RealtimeAudioConfig"/>.</summary>
    public static JsonTypeInfo<RealtimeAudioConfig> RealtimeAudioConfig => Metadata<RealtimeAudioConfig>();
    /// <summary>JSON metadata for <see cref="RealtimeTurnDetection"/>.</summary>
    public static JsonTypeInfo<RealtimeTurnDetection> RealtimeTurnDetection => Metadata<RealtimeTurnDetection>();
    /// <summary>JSON metadata for <see cref="RealtimeManualTurnDetection"/>.</summary>
    public static JsonTypeInfo<RealtimeManualTurnDetection> RealtimeManualTurnDetection => Metadata<RealtimeManualTurnDetection>();
    /// <summary>JSON metadata for <see cref="RealtimeServerVad"/>.</summary>
    public static JsonTypeInfo<RealtimeServerVad> RealtimeServerVad => Metadata<RealtimeServerVad>();
    /// <summary>JSON metadata for <see cref="RealtimeConversationReference"/>.</summary>
    public static JsonTypeInfo<RealtimeConversationReference> RealtimeConversationReference => Metadata<RealtimeConversationReference>();
    /// <summary>JSON metadata for <see cref="RealtimeResumptionOptions"/>.</summary>
    public static JsonTypeInfo<RealtimeResumptionOptions> RealtimeResumptionOptions => Metadata<RealtimeResumptionOptions>();
    /// <summary>JSON metadata for <see cref="RealtimeProviderOptions"/>.</summary>
    public static JsonTypeInfo<RealtimeProviderOptions> RealtimeProviderOptions => Metadata<RealtimeProviderOptions>();
    /// <summary>JSON metadata for <see cref="RealtimeSessionConfig"/>.</summary>
    public static JsonTypeInfo<RealtimeSessionConfig> RealtimeSessionConfig => Metadata<RealtimeSessionConfig>();
    /// <summary>JSON metadata for <see cref="RealtimeContent"/>.</summary>
    public static JsonTypeInfo<RealtimeContent> RealtimeContent => Metadata<RealtimeContent>();
    /// <summary>JSON metadata for <see cref="RealtimeTextContent"/>.</summary>
    public static JsonTypeInfo<RealtimeTextContent> RealtimeTextContent => Metadata<RealtimeTextContent>();
    /// <summary>JSON metadata for <see cref="RealtimeAudioContent"/>.</summary>
    public static JsonTypeInfo<RealtimeAudioContent> RealtimeAudioContent => Metadata<RealtimeAudioContent>();
    /// <summary>JSON metadata for <see cref="RealtimeConversationItem"/>.</summary>
    public static JsonTypeInfo<RealtimeConversationItem> RealtimeConversationItem => Metadata<RealtimeConversationItem>();
    /// <summary>JSON metadata for <see cref="RealtimeMessageItem"/>.</summary>
    public static JsonTypeInfo<RealtimeMessageItem> RealtimeMessageItem => Metadata<RealtimeMessageItem>();
    /// <summary>JSON metadata for <see cref="RealtimeFunctionCallItem"/>.</summary>
    public static JsonTypeInfo<RealtimeFunctionCallItem> RealtimeFunctionCallItem => Metadata<RealtimeFunctionCallItem>();
    /// <summary>JSON metadata for <see cref="RealtimeFunctionOutputItem"/>.</summary>
    public static JsonTypeInfo<RealtimeFunctionOutputItem> RealtimeFunctionOutputItem => Metadata<RealtimeFunctionOutputItem>();
    /// <summary>JSON metadata for <see cref="RealtimeResponseOptions"/>.</summary>
    public static JsonTypeInfo<RealtimeResponseOptions> RealtimeResponseOptions => Metadata<RealtimeResponseOptions>();
    /// <summary>JSON metadata for <see cref="RealtimeToolResult"/>.</summary>
    public static JsonTypeInfo<RealtimeToolResult> RealtimeToolResult => Metadata<RealtimeToolResult>();
    /// <summary>JSON metadata for <see cref="RealtimeCapabilities"/>.</summary>
    public static JsonTypeInfo<RealtimeCapabilities> RealtimeCapabilities => Metadata<RealtimeCapabilities>();
    /// <summary>JSON metadata for <see cref="RealtimeEventIdentity"/>.</summary>
    public static JsonTypeInfo<RealtimeEventIdentity> RealtimeEventIdentity => Metadata<RealtimeEventIdentity>();
    /// <summary>JSON metadata for <see cref="RealtimeSessionEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeSessionEvent> RealtimeSessionEvent => Metadata<RealtimeSessionEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeConversationCreatedEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeConversationCreatedEvent> RealtimeConversationCreatedEvent => Metadata<RealtimeConversationCreatedEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeInputSpeechEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeInputSpeechEvent> RealtimeInputSpeechEvent => Metadata<RealtimeInputSpeechEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeInputBufferEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeInputBufferEvent> RealtimeInputBufferEvent => Metadata<RealtimeInputBufferEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeItemEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeItemEvent> RealtimeItemEvent => Metadata<RealtimeItemEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeContentPartEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeContentPartEvent> RealtimeContentPartEvent => Metadata<RealtimeContentPartEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeTranscriptEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeTranscriptEvent> RealtimeTranscriptEvent => Metadata<RealtimeTranscriptEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeAudioDeltaEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeAudioDeltaEvent> RealtimeAudioDeltaEvent => Metadata<RealtimeAudioDeltaEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeAudioDoneEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeAudioDoneEvent> RealtimeAudioDoneEvent => Metadata<RealtimeAudioDoneEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeAudioArtifact"/>.</summary>
    public static JsonTypeInfo<RealtimeAudioArtifact> RealtimeAudioArtifact => Metadata<RealtimeAudioArtifact>();
    /// <summary>JSON metadata for <see cref="RealtimeTextDeltaEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeTextDeltaEvent> RealtimeTextDeltaEvent => Metadata<RealtimeTextDeltaEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeFunctionArgumentsEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeFunctionArgumentsEvent> RealtimeFunctionArgumentsEvent => Metadata<RealtimeFunctionArgumentsEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeHostedStartedEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeHostedStartedEvent> RealtimeHostedStartedEvent => Metadata<RealtimeHostedStartedEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeHostedProgressEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeHostedProgressEvent> RealtimeHostedProgressEvent => Metadata<RealtimeHostedProgressEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeHostedCompletedEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeHostedCompletedEvent> RealtimeHostedCompletedEvent => Metadata<RealtimeHostedCompletedEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeHostedObservationEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeHostedObservationEvent> RealtimeHostedObservationEvent => Metadata<RealtimeHostedObservationEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeResponseEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeResponseEvent> RealtimeResponseEvent => Metadata<RealtimeResponseEvent>();
    /// <summary>JSON metadata for <see cref="RealtimeErrorEvent"/>.</summary>
    public static JsonTypeInfo<RealtimeErrorEvent> RealtimeErrorEvent => Metadata<RealtimeErrorEvent>();
    /// <summary>JSON metadata for <see cref="VideoProviderOptions"/>.</summary>
    public static JsonTypeInfo<VideoProviderOptions> VideoProviderOptions => Metadata<VideoProviderOptions>();
    /// <summary>JSON metadata for <see cref="VideoRequest"/>.</summary>
    public static JsonTypeInfo<VideoRequest> VideoRequest => Metadata<VideoRequest>();
    /// <summary>JSON metadata for <see cref="VideoGenerationRequest"/>.</summary>
    public static JsonTypeInfo<VideoGenerationRequest> VideoGenerationRequest => Metadata<VideoGenerationRequest>();
    /// <summary>JSON metadata for <see cref="VideoEditRequest"/>.</summary>
    public static JsonTypeInfo<VideoEditRequest> VideoEditRequest => Metadata<VideoEditRequest>();
    /// <summary>JSON metadata for <see cref="VideoExtensionRequest"/>.</summary>
    public static JsonTypeInfo<VideoExtensionRequest> VideoExtensionRequest => Metadata<VideoExtensionRequest>();
    /// <summary>JSON metadata for <see cref="VideoKeyframe"/>.</summary>
    public static JsonTypeInfo<VideoKeyframe> VideoKeyframe => Metadata<VideoKeyframe>();
    /// <summary>JSON metadata for <see cref="VideoReferenceAudio"/>.</summary>
    public static JsonTypeInfo<VideoReferenceAudio> VideoReferenceAudio => Metadata<VideoReferenceAudio>();
    /// <summary>JSON metadata for <see cref="VideoVoiceReference"/>.</summary>
    public static JsonTypeInfo<VideoVoiceReference> VideoVoiceReference => Metadata<VideoVoiceReference>();
    /// <summary>JSON metadata for <see cref="VideoAudioReference"/>.</summary>
    public static JsonTypeInfo<VideoAudioReference> VideoAudioReference => Metadata<VideoAudioReference>();
    /// <summary>JSON metadata for <see cref="VideoJobState"/>.</summary>
    public static JsonTypeInfo<VideoJobState> VideoJobState => Metadata<VideoJobState>();
    /// <summary>JSON metadata for <see cref="VideoOperation"/>.</summary>
    public static JsonTypeInfo<VideoOperation> VideoOperation => Metadata<VideoOperation>();
    /// <summary>JSON metadata for <see cref="VideoJob"/>.</summary>
    public static JsonTypeInfo<VideoJob> VideoJob => Metadata<VideoJob>();
    /// <summary>JSON metadata for <see cref="VideoOutput"/>.</summary>
    public static JsonTypeInfo<VideoOutput> VideoOutput => Metadata<VideoOutput>();
    /// <summary>JSON metadata for <see cref="VideoJobSnapshot"/>.</summary>
    public static JsonTypeInfo<VideoJobSnapshot> VideoJobSnapshot => Metadata<VideoJobSnapshot>();
    /// <summary>JSON metadata for <see cref="VideoCapabilities"/>.</summary>
    public static JsonTypeInfo<VideoCapabilities> VideoCapabilities => Metadata<VideoCapabilities>();
    /// <summary>JSON metadata for <see cref="InferenceStreamLimits"/>.</summary>
    public static JsonTypeInfo<InferenceStreamLimits> InferenceStreamLimits => Metadata<InferenceStreamLimits>();
    /// <summary>JSON metadata for <see cref="LlmExplicitCacheReference"/>.</summary>
    public static JsonTypeInfo<LlmExplicitCacheReference> LlmExplicitCacheReference => Metadata<LlmExplicitCacheReference>();
    /// <summary>JSON metadata for <see cref="LlmClientToolDefinition"/>.</summary>
    public static JsonTypeInfo<LlmClientToolDefinition> LlmClientToolDefinition => Metadata<LlmClientToolDefinition>();
    /// <summary>JSON metadata for <see cref="LlmShellSkill"/>.</summary>
    public static JsonTypeInfo<LlmShellSkill> LlmShellSkill => Metadata<LlmShellSkill>();
    /// <summary>JSON metadata for <see cref="LlmShellTool"/>.</summary>
    public static JsonTypeInfo<LlmShellTool> LlmShellTool => Metadata<LlmShellTool>();
    /// <summary>JSON metadata for <see cref="LlmClientToolCall"/>.</summary>
    public static JsonTypeInfo<LlmClientToolCall> LlmClientToolCall => Metadata<LlmClientToolCall>();
    /// <summary>JSON metadata for <see cref="LlmToolCall"/>.</summary>
    public static JsonTypeInfo<LlmToolCall> LlmToolCall => Metadata<LlmToolCall>();
    /// <summary>JSON metadata for <see cref="LlmShellCall"/>.</summary>
    public static JsonTypeInfo<LlmShellCall> LlmShellCall => Metadata<LlmShellCall>();
    /// <summary>JSON metadata for <see cref="LlmClientToolResult"/>.</summary>
    public static JsonTypeInfo<LlmClientToolResult> LlmClientToolResult => Metadata<LlmClientToolResult>();
    /// <summary>JSON metadata for <see cref="LlmToolResult"/>.</summary>
    public static JsonTypeInfo<LlmToolResult> LlmToolResult => Metadata<LlmToolResult>();
    /// <summary>JSON metadata for <see cref="LlmShellCommandResult"/>.</summary>
    public static JsonTypeInfo<LlmShellCommandResult> LlmShellCommandResult => Metadata<LlmShellCommandResult>();
    /// <summary>JSON metadata for <see cref="LlmShellCallResult"/>.</summary>
    public static JsonTypeInfo<LlmShellCallResult> LlmShellCallResult => Metadata<LlmShellCallResult>();
    /// <summary>JSON metadata for <see cref="LlmShellOutcome"/>.</summary>
    public static JsonTypeInfo<LlmShellOutcome> LlmShellOutcome => Metadata<LlmShellOutcome>();
    /// <summary>JSON metadata for <see cref="LlmShellExitedOutcome"/>.</summary>
    public static JsonTypeInfo<LlmShellExitedOutcome> LlmShellExitedOutcome => Metadata<LlmShellExitedOutcome>();
    /// <summary>JSON metadata for <see cref="LlmShellTimedOutOutcome"/>.</summary>
    public static JsonTypeInfo<LlmShellTimedOutOutcome> LlmShellTimedOutOutcome => Metadata<LlmShellTimedOutOutcome>();
    /// <summary>JSON metadata for <see cref="LlmShellCallEvent"/>.</summary>
    public static JsonTypeInfo<LlmShellCallEvent> LlmShellCallEvent => Metadata<LlmShellCallEvent>();
    private static JsonTypeInfo<T> Metadata<T>() => CreateOptions().GetTypeInfo<T>();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(InferenceJsonContext.Default.Options) { TypeInfoResolver = new HostedTypeInfoResolver() };
        options.Converters.Add(new MediaSourceKindJsonConverter());
        options.Converters.Add(new ResourceOperationPhaseJsonConverter());
        options.Converters.Add(new ResourceCommitOutcomeJsonConverter());
        options.Converters.Add(new CommonProviderControlsJsonConverter());
        options.Converters.Add(new VideoJobStateJsonConverter());
        options.Converters.Add(new VideoOperationJsonConverter());
        options.Converters.Add(new VideoProviderOptionsJsonConverter());
        options.Converters.Add(new VideoInferenceJsonConverter());
        options.Converters.Add(new SpeechAudioEncodingJsonConverter());
        options.Converters.Add(new SpeechTranscriptionClientJsonConverter());
        options.Converters.Add(new SpeechTranscriptionSessionJsonConverter());
        options.Converters.Add(new RealtimeEnumJsonConverter<RealtimeAudioEncoding>());
        options.Converters.Add(new RealtimeEnumJsonConverter<RealtimeAudioTransport>());
        options.Converters.Add(new RealtimeEnumJsonConverter<RealtimeModality>());
        options.Converters.Add(new RealtimeEnumJsonConverter<RealtimeMessageRole>());
        options.Converters.Add(new RealtimeProviderOptionsJsonConverter());
        options.Converters.Add(new RealtimeSessionJsonConverter());
        options.MakeReadOnly();
        return options;
    }

    private sealed class HostedTypeInfoResolver : IJsonTypeInfoResolver
    {
        private readonly HostedImageDataJsonConverter imageData = new((JsonConverter<ReadOnlyMemory<byte>>)InferenceJsonContext.Default.Options.GetTypeInfo<ReadOnlyMemory<byte>>().Converter);
        private readonly RealtimeNullableAudioJsonConverter observedAudio;
        public HostedTypeInfoResolver() => observedAudio = new(imageData);

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            if (type == typeof(ISpeechTranscriptionClient)) return JsonMetadataServices.CreateValueInfo<ISpeechTranscriptionClient>(options, new SpeechTranscriptionClientJsonConverter());
            if (type == typeof(ISpeechTranscriptionSession)) return JsonMetadataServices.CreateValueInfo<ISpeechTranscriptionSession>(options, new SpeechTranscriptionSessionJsonConverter());
            var metadata = ((IJsonTypeInfoResolver)InferenceJsonContext.Default).GetTypeInfo(type, options);
            if ((type == typeof(LlmHostedImageOutput) || type == typeof(BinaryMediaSource) || type == typeof(BinarySpeechInput)) && metadata is not null)
                metadata.Properties.Single(property => property.Name == "Data").CustomConverter = imageData;
            if (type == typeof(RealtimeAudioContent) && metadata is not null)
                metadata.Properties.Single(property => property.Name == "Audio").CustomConverter = observedAudio;
            if (type == typeof(RealtimeAudioDeltaEvent) && metadata is not null)
                metadata.Properties.Single(property => property.Name == "Audio").CustomConverter = imageData;
            return metadata;
        }
    }

    /// <summary>JSON metadata for <see cref="LlmMessageAttachment"/>.</summary>
    public static JsonTypeInfo<LlmMessageAttachment> LlmMessageAttachment => Metadata<LlmMessageAttachment>();
    /// <summary>JSON metadata for <see cref="LlmAttachment"/>.</summary>
    public static JsonTypeInfo<LlmAttachment> LlmAttachment => Metadata<LlmAttachment>();
    /// <summary>JSON metadata for <see cref="LlmFileReference"/>.</summary>
    public static JsonTypeInfo<LlmFileReference> LlmFileReference => Metadata<LlmFileReference>();
    /// <summary>JSON metadata for <see cref="LlmResourceAttachment"/>.</summary>
    public static JsonTypeInfo<LlmResourceAttachment> LlmResourceAttachment => Metadata<LlmResourceAttachment>();
    /// <summary>JSON metadata for <see cref="LlmToolOutputSessionResource"/>.</summary>
    public static JsonTypeInfo<LlmToolOutputSessionResource> LlmToolOutputSessionResource => Metadata<LlmToolOutputSessionResource>();
    /// <summary>JSON metadata for <see cref="InferenceMediaSourceKind"/>.</summary>
    public static JsonTypeInfo<InferenceMediaSourceKind> InferenceMediaSourceKind => Metadata<InferenceMediaSourceKind>();
    /// <summary>JSON metadata for <see cref="InferenceMediaSource"/>.</summary>
    public static JsonTypeInfo<InferenceMediaSource> InferenceMediaSource => Metadata<InferenceMediaSource>();
    /// <summary>JSON metadata for <see cref="BinaryMediaSource"/>.</summary>
    public static JsonTypeInfo<BinaryMediaSource> BinaryMediaSource => Metadata<BinaryMediaSource>();
    /// <summary>JSON metadata for <see cref="UrlMediaSource"/>.</summary>
    public static JsonTypeInfo<UrlMediaSource> UrlMediaSource => Metadata<UrlMediaSource>();
    /// <summary>JSON metadata for <see cref="ProviderFileReference"/>.</summary>
    public static JsonTypeInfo<ProviderFileReference> ProviderFileReference => Metadata<ProviderFileReference>();
    /// <summary>JSON metadata for <see cref="FileMediaSource"/>.</summary>
    public static JsonTypeInfo<FileMediaSource> FileMediaSource => Metadata<FileMediaSource>();
    /// <summary>JSON metadata for <see cref="ResourceMediaSource"/>.</summary>
    public static JsonTypeInfo<ResourceMediaSource> ResourceMediaSource => Metadata<ResourceMediaSource>();
    /// <summary>JSON metadata for <see cref="InferenceResourceScope"/>.</summary>
    public static JsonTypeInfo<InferenceResourceScope> InferenceResourceScope => Metadata<InferenceResourceScope>();
    /// <summary>JSON metadata for <see cref="InferenceResourceFailure"/>.</summary>
    public static JsonTypeInfo<InferenceResourceFailure> InferenceResourceFailure => Metadata<InferenceResourceFailure>();
    /// <summary>JSON metadata for <see cref="InferenceProvenance"/>.</summary>
    public static JsonTypeInfo<InferenceProvenance> Provenance => Metadata<InferenceProvenance>();
    /// <summary>JSON metadata for <see cref="LlmInferenceRequest"/>.</summary>
    public static JsonTypeInfo<LlmInferenceRequest> LlmRequest => Metadata<LlmInferenceRequest>();
    /// <summary>JSON metadata for <see cref="LlmInferenceResult"/>.</summary>
    public static JsonTypeInfo<LlmInferenceResult> LlmResult => Metadata<LlmInferenceResult>();
    /// <summary>JSON metadata for <see cref="LlmMessage"/>.</summary>
    public static JsonTypeInfo<LlmMessage> LlmMessage => Metadata<LlmMessage>();
    /// <summary>JSON metadata for <see cref="LlmContinuationState"/>.</summary>
    public static JsonTypeInfo<LlmContinuationState> ContinuationState => Metadata<LlmContinuationState>();
    /// <summary>JSON metadata for <see cref="LlmCapabilities"/>.</summary>
    public static JsonTypeInfo<LlmCapabilities> LlmCapabilities => Metadata<LlmCapabilities>();
    /// <summary>JSON metadata for <see cref="LlmModelInfo"/>.</summary>
    public static JsonTypeInfo<LlmModelInfo> LlmModel => Metadata<LlmModelInfo>();
    /// <summary>JSON metadata for <see cref="LlmToolDefinition"/>.</summary>
    public static JsonTypeInfo<LlmToolDefinition> ToolDefinition => Metadata<LlmToolDefinition>();
    /// <summary>JSON metadata for <see cref="LlmToolOutput"/>.</summary>
    public static JsonTypeInfo<LlmToolOutput> ToolOutput => Metadata<LlmToolOutput>();
    /// <summary>JSON metadata for <see cref="LlmToolOutputPart"/>.</summary>
    public static JsonTypeInfo<LlmToolOutputPart> ToolOutputPart => Metadata<LlmToolOutputPart>();
    /// <summary>JSON metadata for <see cref="LlmTextDeltaEvent"/>.</summary>
    public static JsonTypeInfo<LlmTextDeltaEvent> TextDelta => Metadata<LlmTextDeltaEvent>();
    /// <summary>JSON metadata for <see cref="LlmStructuredOutputSnapshotEvent"/>.</summary>
    public static JsonTypeInfo<LlmStructuredOutputSnapshotEvent> StructuredOutputSnapshot => Metadata<LlmStructuredOutputSnapshotEvent>();
    /// <summary>JSON metadata for <see cref="LlmReasoningDeltaEvent"/>.</summary>
    public static JsonTypeInfo<LlmReasoningDeltaEvent> ReasoningDelta => Metadata<LlmReasoningDeltaEvent>();
    /// <summary>JSON metadata for <see cref="LlmRefusalDeltaEvent"/>.</summary>
    public static JsonTypeInfo<LlmRefusalDeltaEvent> RefusalDelta => Metadata<LlmRefusalDeltaEvent>();
    /// <summary>JSON metadata for <see cref="LlmActivityEvent"/>.</summary>
    public static JsonTypeInfo<LlmActivityEvent> Activity => Metadata<LlmActivityEvent>();
    /// <summary>JSON metadata for <see cref="LlmToolCallStartedEvent"/>.</summary>
    public static JsonTypeInfo<LlmToolCallStartedEvent> ToolCallStarted => Metadata<LlmToolCallStartedEvent>();
    /// <summary>JSON metadata for <see cref="LlmToolCallArgumentsDeltaEvent"/>.</summary>
    public static JsonTypeInfo<LlmToolCallArgumentsDeltaEvent> ToolCallArgumentsDelta => Metadata<LlmToolCallArgumentsDeltaEvent>();
    /// <summary>JSON metadata for <see cref="LlmToolCallCompletedEvent"/>.</summary>
    public static JsonTypeInfo<LlmToolCallCompletedEvent> ToolCallCompleted => Metadata<LlmToolCallCompletedEvent>();
    /// <summary>JSON metadata for <see cref="LlmUsageDeltaEvent"/>.</summary>
    public static JsonTypeInfo<LlmUsageDeltaEvent> UsageDelta => Metadata<LlmUsageDeltaEvent>();
    /// <summary>JSON metadata for <see cref="LlmUsage"/>.</summary>
    public static JsonTypeInfo<LlmUsage> LlmUsage => Metadata<LlmUsage>();
    /// <summary>JSON metadata for <see cref="LlmHostedToolKind"/>.</summary>
    public static JsonTypeInfo<LlmHostedToolKind> LlmHostedToolKind => Metadata<LlmHostedToolKind>();
    /// <summary>JSON metadata for <see cref="LlmHostedToolPhase"/>.</summary>
    public static JsonTypeInfo<LlmHostedToolPhase> LlmHostedToolPhase => Metadata<LlmHostedToolPhase>();
    /// <summary>JSON metadata for <see cref="LlmImageGenerationAction"/>.</summary>
    public static JsonTypeInfo<LlmImageGenerationAction> LlmImageGenerationAction => Metadata<LlmImageGenerationAction>();
    /// <summary>JSON metadata for <see cref="LlmHostedTool"/>.</summary>
    public static JsonTypeInfo<LlmHostedTool> LlmHostedTool => Metadata<LlmHostedTool>();
    /// <summary>JSON metadata for <see cref="LlmWebSearchTool"/>.</summary>
    public static JsonTypeInfo<LlmWebSearchTool> LlmWebSearchTool => Metadata<LlmWebSearchTool>();
    /// <summary>JSON metadata for <see cref="LlmWebSearchLocation"/>.</summary>
    public static JsonTypeInfo<LlmWebSearchLocation> LlmWebSearchLocation => Metadata<LlmWebSearchLocation>();
    /// <summary>JSON metadata for <see cref="LlmCodeExecutionTool"/>.</summary>
    public static JsonTypeInfo<LlmCodeExecutionTool> LlmCodeExecutionTool => Metadata<LlmCodeExecutionTool>();
    /// <summary>JSON metadata for <see cref="LlmImageGenerationTool"/>.</summary>
    public static JsonTypeInfo<LlmImageGenerationTool> LlmImageGenerationTool => Metadata<LlmImageGenerationTool>();
    /// <summary>JSON metadata for <see cref="LlmDocumentSearchTool"/>.</summary>
    public static JsonTypeInfo<LlmDocumentSearchTool> LlmDocumentSearchTool => Metadata<LlmDocumentSearchTool>();
    /// <summary>JSON metadata for <see cref="LlmRemoteMcpTool"/>.</summary>
    public static JsonTypeInfo<LlmRemoteMcpTool> LlmRemoteMcpTool => Metadata<LlmRemoteMcpTool>();
    /// <summary>JSON metadata for <see cref="LlmHostedToolOutput"/>.</summary>
    public static JsonTypeInfo<LlmHostedToolOutput> LlmHostedToolOutput => Metadata<LlmHostedToolOutput>();
    /// <summary>JSON metadata for <see cref="LlmHostedSearchSource"/>.</summary>
    public static JsonTypeInfo<LlmHostedSearchSource> LlmHostedSearchSource => Metadata<LlmHostedSearchSource>();
    /// <summary>JSON metadata for <see cref="LlmHostedSearchOutput"/>.</summary>
    public static JsonTypeInfo<LlmHostedSearchOutput> LlmHostedSearchOutput => Metadata<LlmHostedSearchOutput>();
    /// <summary>JSON metadata for <see cref="LlmHostedCodeOutput"/>.</summary>
    public static JsonTypeInfo<LlmHostedCodeOutput> LlmHostedCodeOutput => Metadata<LlmHostedCodeOutput>();
    /// <summary>JSON metadata for <see cref="LlmHostedDocumentMatch"/>.</summary>
    public static JsonTypeInfo<LlmHostedDocumentMatch> LlmHostedDocumentMatch => Metadata<LlmHostedDocumentMatch>();
    /// <summary>JSON metadata for <see cref="LlmHostedDocumentSearchOutput"/>.</summary>
    public static JsonTypeInfo<LlmHostedDocumentSearchOutput> LlmHostedDocumentSearchOutput => Metadata<LlmHostedDocumentSearchOutput>();
    /// <summary>JSON metadata for <see cref="LlmHostedMcpOutput"/>.</summary>
    public static JsonTypeInfo<LlmHostedMcpOutput> LlmHostedMcpOutput => Metadata<LlmHostedMcpOutput>();
    /// <summary>JSON metadata for <see cref="LlmHostedImageOutput"/>.</summary>
    public static JsonTypeInfo<LlmHostedImageOutput> LlmHostedImageOutput => Metadata<LlmHostedImageOutput>();
    /// <summary>JSON metadata for <see cref="LlmHostedToolCall"/>.</summary>
    public static JsonTypeInfo<LlmHostedToolCall> LlmHostedToolCall => Metadata<LlmHostedToolCall>();
    /// <summary>JSON metadata for <see cref="LlmHostedToolCallStartedEvent"/>.</summary>
    public static JsonTypeInfo<LlmHostedToolCallStartedEvent> LlmHostedToolCallStartedEvent => Metadata<LlmHostedToolCallStartedEvent>();
    /// <summary>JSON metadata for <see cref="LlmHostedToolCallProgressEvent"/>.</summary>
    public static JsonTypeInfo<LlmHostedToolCallProgressEvent> LlmHostedToolCallProgressEvent => Metadata<LlmHostedToolCallProgressEvent>();
    /// <summary>JSON metadata for <see cref="LlmHostedToolCallCompletedEvent"/>.</summary>
    public static JsonTypeInfo<LlmHostedToolCallCompletedEvent> LlmHostedToolCallCompletedEvent => Metadata<LlmHostedToolCallCompletedEvent>();
    /// <summary>JSON metadata for <see cref="LlmCitation"/>.</summary>
    public static JsonTypeInfo<LlmCitation> LlmCitation => Metadata<LlmCitation>();
    /// <summary>JSON metadata for <see cref="LlmCitationEvent"/>.</summary>
    public static JsonTypeInfo<LlmCitationEvent> LlmCitationEvent => Metadata<LlmCitationEvent>();
    /// <summary>JSON metadata for <see cref="LlmCompletionEvent"/>.</summary>
    public static JsonTypeInfo<LlmCompletionEvent> Completion => Metadata<LlmCompletionEvent>();
    /// <summary>JSON metadata for <see cref="DecisionQuestion"/>.</summary>
    public static JsonTypeInfo<DecisionQuestion> DecisionQuestion => Metadata<DecisionQuestion>();
    /// <summary>JSON metadata for <see cref="DecisionAnswer"/>.</summary>
    public static JsonTypeInfo<DecisionAnswer> DecisionAnswer => Metadata<DecisionAnswer>();
    /// <summary>JSON metadata for <see cref="DecisionInferenceRequest"/>.</summary>
    public static JsonTypeInfo<DecisionInferenceRequest> DecisionRequest => Metadata<DecisionInferenceRequest>();
    /// <summary>JSON metadata for <see cref="DecisionInferenceResult"/>.</summary>
    public static JsonTypeInfo<DecisionInferenceResult> DecisionResult => Metadata<DecisionInferenceResult>();
    /// <summary>JSON metadata for <see cref="DecisionCapabilities"/>.</summary>
    public static JsonTypeInfo<DecisionCapabilities> DecisionCapabilities => Metadata<DecisionCapabilities>();
    /// <summary>JSON metadata for <see cref="DecisionInputMeasurement"/>.</summary>
    public static JsonTypeInfo<DecisionInputMeasurement> DecisionMeasurement => Metadata<DecisionInputMeasurement>();
    /// <summary>JSON metadata for <see cref="EmbeddingRequest"/>.</summary>
    public static JsonTypeInfo<EmbeddingRequest> EmbeddingRequest => Metadata<EmbeddingRequest>();
    /// <summary>JSON metadata for <see cref="EmbeddingResult"/>.</summary>
    public static JsonTypeInfo<EmbeddingResult> EmbeddingResult => Metadata<EmbeddingResult>();
    /// <summary>JSON metadata for <see cref="EmbeddingCapabilities"/>.</summary>
    public static JsonTypeInfo<EmbeddingCapabilities> EmbeddingCapabilities => Metadata<EmbeddingCapabilities>();
    /// <summary>JSON metadata for arrays of <see cref="EmbeddingOverLimitInput"/>.</summary>
    public static JsonTypeInfo<EmbeddingOverLimitInput[]> EmbeddingOverLimitInputs => Metadata<EmbeddingOverLimitInput[]>();
    /// <summary>JSON metadata for <see cref="ImageGenerationRequest"/>.</summary>
    public static JsonTypeInfo<ImageGenerationRequest> ImageRequest => Metadata<ImageGenerationRequest>();
    /// <summary>JSON metadata for <see cref="ImageGenerationResult"/>.</summary>
    public static JsonTypeInfo<ImageGenerationResult> ImageResult => Metadata<ImageGenerationResult>();
    /// <summary>JSON metadata for <see cref="ImageGenerationProgress"/>.</summary>
    public static JsonTypeInfo<ImageGenerationProgress> ImageProgress => Metadata<ImageGenerationProgress>();
    /// <summary>JSON metadata for <see cref="SetupStatus"/>.</summary>
    public static JsonTypeInfo<SetupStatus> SetupStatus => Metadata<SetupStatus>();
    /// <summary>JSON metadata for <see cref="SetupValidation"/>.</summary>
    public static JsonTypeInfo<SetupValidation> SetupValidation => Metadata<SetupValidation>();
    /// <summary>JSON metadata for <see cref="SetupConfigurationIdentity"/>.</summary>
    public static JsonTypeInfo<SetupConfigurationIdentity> SetupIdentity => Metadata<SetupConfigurationIdentity>();
    /// <summary>JSON metadata for <see cref="SetupCommitResult"/>.</summary>
    public static JsonTypeInfo<SetupCommitResult> SetupCommit => Metadata<SetupCommitResult>();
    /// <summary>JSON metadata for <see cref="SetupEditResult"/>.</summary>
    public static JsonTypeInfo<SetupEditResult> SetupEdit => Metadata<SetupEditResult>();
    /// <summary>JSON metadata for <see cref="SetupActionDescriptor"/>.</summary>
    public static JsonTypeInfo<SetupActionDescriptor> SetupAction => Metadata<SetupActionDescriptor>();
    /// <summary>JSON metadata for <see cref="SetupProgress"/>.</summary>
    public static JsonTypeInfo<SetupProgress> SetupProgress => Metadata<SetupProgress>();
    /// <summary>JSON metadata for <see cref="SetupChoicePage"/>.</summary>
    public static JsonTypeInfo<SetupChoicePage> SetupChoices => Metadata<SetupChoicePage>();
    /// <summary>JSON metadata for <see cref="InferenceResourceReference"/>.</summary>
    public static JsonTypeInfo<InferenceResourceReference> InferenceResourceReference => Metadata<InferenceResourceReference>();
    /// <summary>JSON metadata for <see cref="InferenceResourceDescriptor"/>.</summary>
    public static JsonTypeInfo<InferenceResourceDescriptor> InferenceResourceDescriptor => Metadata<InferenceResourceDescriptor>();
    /// <summary>JSON metadata for <see cref="InferenceResourceCreate"/>.</summary>
    public static JsonTypeInfo<InferenceResourceCreate> InferenceResourceCreate => Metadata<InferenceResourceCreate>();
    /// <summary>JSON metadata for <see cref="InferenceResourceSelectionVersion"/>.</summary>
    public static JsonTypeInfo<InferenceResourceSelectionVersion> InferenceResourceSelectionVersion => Metadata<InferenceResourceSelectionVersion>();
    /// <summary>JSON metadata for <see cref="InferenceResourceSelection"/>.</summary>
    public static JsonTypeInfo<InferenceResourceSelection> InferenceResourceSelection => Metadata<InferenceResourceSelection>();
    /// <summary>JSON metadata for <see cref="InferenceResourceCommitOptions"/>.</summary>
    public static JsonTypeInfo<InferenceResourceCommitOptions> InferenceResourceCommitOptions => Metadata<InferenceResourceCommitOptions>();
    /// <summary>JSON metadata for <see cref="InferenceResourceCommitResult"/>.</summary>
    public static JsonTypeInfo<InferenceResourceCommitResult> InferenceResourceCommitResult => Metadata<InferenceResourceCommitResult>();
    /// <summary>JSON metadata for <see cref="InferenceResourceOperationPhase"/>.</summary>
    public static JsonTypeInfo<InferenceResourceOperationPhase> InferenceResourceOperationPhase => Metadata<InferenceResourceOperationPhase>();
    /// <summary>JSON metadata for <see cref="InferenceResourceCommitOutcome"/>.</summary>
    public static JsonTypeInfo<InferenceResourceCommitOutcome> InferenceResourceCommitOutcome => Metadata<InferenceResourceCommitOutcome>();
    /// <summary>JSON metadata for <see cref="InferenceResourceOperationFailure"/>.</summary>
    public static JsonTypeInfo<InferenceResourceOperationFailure> InferenceResourceOperationFailure => Metadata<InferenceResourceOperationFailure>();
    /// <summary>JSON metadata for <see cref="ImageSize"/>.</summary>
    public static JsonTypeInfo<ImageSize> ImageSize => Metadata<ImageSize>();
    /// <summary>JSON metadata for <see cref="ImageCapabilities"/>.</summary>
    public static JsonTypeInfo<ImageCapabilities> ImageCapabilities => Metadata<ImageCapabilities>();
    /// <summary>JSON metadata for <see cref="GeneratedImage"/>.</summary>
    public static JsonTypeInfo<GeneratedImage> GeneratedImage => Metadata<GeneratedImage>();
    /// <summary>JSON metadata for <see cref="ImageGenerationStage"/>.</summary>
    public static JsonTypeInfo<ImageGenerationStage> ImageGenerationStage => Metadata<ImageGenerationStage>();
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(ResourceAccessJsonConverter), typeof(ResourceReadHandleJsonConverter), typeof(ResourceWriteHandleJsonConverter), typeof(ProviderAuthorizationJsonConverter), typeof(HostedToolKindJsonConverter), typeof(HostedToolPhaseJsonConverter), typeof(ImageGenerationActionJsonConverter)])]
[JsonSerializable(typeof(InferenceProvenance))]
[JsonSerializable(typeof(SpeechAudioEncoding))]
[JsonSerializable(typeof(SpeechTranscriptionInput))]
[JsonSerializable(typeof(BinarySpeechInput))]
[JsonSerializable(typeof(UrlSpeechInput))]
[JsonSerializable(typeof(ResourceSpeechInput))]
[JsonSerializable(typeof(SpeechTranscriptionOptions))]
[JsonSerializable(typeof(SpeechStreamOptions))]
[JsonSerializable(typeof(SpeechWord))]
[JsonSerializable(typeof(SpeechChannelTranscript))]
[JsonSerializable(typeof(SpeechTranscriptionResult))]
[JsonSerializable(typeof(SpeechTranscriptCreatedEvent))]
[JsonSerializable(typeof(SpeechTranscriptPartialEvent))]
[JsonSerializable(typeof(SpeechTranscriptDoneEvent))]
[JsonSerializable(typeof(SpeechTranscriptErrorEvent))]
[JsonSerializable(typeof(SpeechTranscriptionCapabilities))]
[JsonSerializable(typeof(SpeechTranscriptionModeCapabilities))]
[JsonSerializable(typeof(RealtimeAudioEncoding))]
[JsonSerializable(typeof(RealtimeAudioTransport))]
[JsonSerializable(typeof(RealtimeModality))]
[JsonSerializable(typeof(RealtimeMessageRole))]
[JsonSerializable(typeof(RealtimeAudioConfig))]
[JsonSerializable(typeof(RealtimeTurnDetection))]
[JsonSerializable(typeof(RealtimeManualTurnDetection))]
[JsonSerializable(typeof(RealtimeServerVad))]
[JsonSerializable(typeof(RealtimeConversationReference))]
[JsonSerializable(typeof(RealtimeResumptionOptions))]
[JsonSerializable(typeof(RealtimeProviderOptions))]
[JsonSerializable(typeof(RealtimeSessionConfig))]
[JsonSerializable(typeof(RealtimeContent))]
[JsonSerializable(typeof(RealtimeTextContent))]
[JsonSerializable(typeof(RealtimeAudioContent))]
[JsonSerializable(typeof(RealtimeConversationItem))]
[JsonSerializable(typeof(RealtimeMessageItem))]
[JsonSerializable(typeof(RealtimeFunctionCallItem))]
[JsonSerializable(typeof(RealtimeFunctionOutputItem))]
[JsonSerializable(typeof(RealtimeResponseOptions))]
[JsonSerializable(typeof(RealtimeToolResult))]
[JsonSerializable(typeof(RealtimeCapabilities))]
[JsonSerializable(typeof(RealtimeEventIdentity))]
[JsonSerializable(typeof(RealtimeSessionEvent))]
[JsonSerializable(typeof(RealtimeConversationCreatedEvent))]
[JsonSerializable(typeof(RealtimeInputSpeechEvent))]
[JsonSerializable(typeof(RealtimeInputBufferEvent))]
[JsonSerializable(typeof(RealtimeItemEvent))]
[JsonSerializable(typeof(RealtimeContentPartEvent))]
[JsonSerializable(typeof(RealtimeTranscriptEvent))]
[JsonSerializable(typeof(RealtimeAudioDeltaEvent))]
[JsonSerializable(typeof(RealtimeAudioDoneEvent))]
[JsonSerializable(typeof(RealtimeAudioArtifact))]
[JsonSerializable(typeof(RealtimeTextDeltaEvent))]
[JsonSerializable(typeof(RealtimeFunctionArgumentsEvent))]
[JsonSerializable(typeof(RealtimeHostedStartedEvent))]
[JsonSerializable(typeof(RealtimeHostedProgressEvent))]
[JsonSerializable(typeof(RealtimeHostedCompletedEvent))]
[JsonSerializable(typeof(RealtimeHostedObservationEvent))]
[JsonSerializable(typeof(RealtimeResponseEvent))]
[JsonSerializable(typeof(RealtimeErrorEvent))]
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
[JsonSerializable(typeof(InferenceStreamLimits))]
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
[JsonSerializable(typeof(LlmProviderControls))]
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
[JsonSerializable(typeof(LlmWebSearchLocation))]
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
[JsonSerializable(typeof(EmbeddingRequest))]
[JsonSerializable(typeof(EmbeddingResult))]
[JsonSerializable(typeof(EmbeddingCapabilities))]
[JsonSerializable(typeof(EmbeddingOverLimitInput[]))]
[JsonSerializable(typeof(ImageGenerationRequest))]
[JsonSerializable(typeof(ImageGenerationResult))]
[JsonSerializable(typeof(ImageGenerationProgress))]
[JsonSerializable(typeof(ImageSize))]
[JsonSerializable(typeof(ImageCapabilities))]
[JsonSerializable(typeof(GeneratedImage))]
[JsonSerializable(typeof(ImageGenerationStage))]
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
