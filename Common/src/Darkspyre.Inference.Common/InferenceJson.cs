using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Common.Resources;
using Darkspyre.Inference.Common.Setup;

namespace Darkspyre.Inference.Common;

/// <summary>Common-owned metadata for closed data contracts. No reflection fallback is installed.</summary>
public static class InferenceJson
{
    public static JsonTypeInfo<InferenceProvenance> Provenance => InferenceJsonContext.Default.InferenceProvenance;
    public static JsonTypeInfo<LlmInferenceRequest> LlmRequest => InferenceJsonContext.Default.LlmInferenceRequest;
    public static JsonTypeInfo<LlmInferenceResult> LlmResult => InferenceJsonContext.Default.LlmInferenceResult;
    public static JsonTypeInfo<LlmMessage> LlmMessage => InferenceJsonContext.Default.LlmMessage;
    public static JsonTypeInfo<LlmContinuationState> ContinuationState => InferenceJsonContext.Default.LlmContinuationState;
    public static JsonTypeInfo<LlmCapabilities> LlmCapabilities => InferenceJsonContext.Default.LlmCapabilities;
    public static JsonTypeInfo<LlmModelInfo> LlmModel => InferenceJsonContext.Default.LlmModelInfo;
    public static JsonTypeInfo<LlmToolDefinition> ToolDefinition => InferenceJsonContext.Default.LlmToolDefinition;
    public static JsonTypeInfo<LlmToolOutput> ToolOutput => InferenceJsonContext.Default.LlmToolOutput;
    public static JsonTypeInfo<LlmToolOutputPart> ToolOutputPart => InferenceJsonContext.Default.LlmToolOutputPart;
    public static JsonTypeInfo<LlmTextDeltaEvent> TextDelta => InferenceJsonContext.Default.LlmTextDeltaEvent;
    public static JsonTypeInfo<LlmStructuredOutputSnapshotEvent> StructuredOutputSnapshot => InferenceJsonContext.Default.LlmStructuredOutputSnapshotEvent;
    public static JsonTypeInfo<LlmReasoningDeltaEvent> ReasoningDelta => InferenceJsonContext.Default.LlmReasoningDeltaEvent;
    public static JsonTypeInfo<LlmRefusalDeltaEvent> RefusalDelta => InferenceJsonContext.Default.LlmRefusalDeltaEvent;
    public static JsonTypeInfo<LlmActivityEvent> Activity => InferenceJsonContext.Default.LlmActivityEvent;
    public static JsonTypeInfo<LlmToolCallStartedEvent> ToolCallStarted => InferenceJsonContext.Default.LlmToolCallStartedEvent;
    public static JsonTypeInfo<LlmToolCallArgumentsDeltaEvent> ToolCallArgumentsDelta => InferenceJsonContext.Default.LlmToolCallArgumentsDeltaEvent;
    public static JsonTypeInfo<LlmToolCallCompletedEvent> ToolCallCompleted => InferenceJsonContext.Default.LlmToolCallCompletedEvent;
    public static JsonTypeInfo<LlmUsageDeltaEvent> UsageDelta => InferenceJsonContext.Default.LlmUsageDeltaEvent;
    public static JsonTypeInfo<LlmUsage> LlmUsage => InferenceJsonContext.Default.LlmUsage;
    public static JsonTypeInfo<LlmHostedToolKind> LlmHostedToolKind => InferenceJsonContext.Default.LlmHostedToolKind;
    public static JsonTypeInfo<LlmHostedToolPhase> LlmHostedToolPhase => InferenceJsonContext.Default.LlmHostedToolPhase;
    public static JsonTypeInfo<LlmImageGenerationAction> LlmImageGenerationAction => InferenceJsonContext.Default.LlmImageGenerationAction;
    public static JsonTypeInfo<LlmHostedTool> LlmHostedTool => InferenceJsonContext.Default.LlmHostedTool;
    public static JsonTypeInfo<LlmWebSearchTool> LlmWebSearchTool => InferenceJsonContext.Default.LlmWebSearchTool;
    public static JsonTypeInfo<LlmCodeExecutionTool> LlmCodeExecutionTool => InferenceJsonContext.Default.LlmCodeExecutionTool;
    public static JsonTypeInfo<LlmImageGenerationTool> LlmImageGenerationTool => InferenceJsonContext.Default.LlmImageGenerationTool;
    public static JsonTypeInfo<LlmDocumentSearchTool> LlmDocumentSearchTool => InferenceJsonContext.Default.LlmDocumentSearchTool;
    public static JsonTypeInfo<LlmRemoteMcpTool> LlmRemoteMcpTool => InferenceJsonContext.Default.LlmRemoteMcpTool;
    public static JsonTypeInfo<LlmHostedToolOutput> LlmHostedToolOutput => InferenceJsonContext.Default.LlmHostedToolOutput;
    public static JsonTypeInfo<LlmHostedSearchSource> LlmHostedSearchSource => InferenceJsonContext.Default.LlmHostedSearchSource;
    public static JsonTypeInfo<LlmHostedSearchOutput> LlmHostedSearchOutput => InferenceJsonContext.Default.LlmHostedSearchOutput;
    public static JsonTypeInfo<LlmHostedCodeOutput> LlmHostedCodeOutput => InferenceJsonContext.Default.LlmHostedCodeOutput;
    public static JsonTypeInfo<LlmHostedDocumentMatch> LlmHostedDocumentMatch => InferenceJsonContext.Default.LlmHostedDocumentMatch;
    public static JsonTypeInfo<LlmHostedDocumentSearchOutput> LlmHostedDocumentSearchOutput => InferenceJsonContext.Default.LlmHostedDocumentSearchOutput;
    public static JsonTypeInfo<LlmHostedMcpOutput> LlmHostedMcpOutput => InferenceJsonContext.Default.LlmHostedMcpOutput;
    public static JsonTypeInfo<LlmHostedImageOutput> LlmHostedImageOutput => InferenceJsonContext.Default.LlmHostedImageOutput;
    public static JsonTypeInfo<LlmHostedToolCall> LlmHostedToolCall => InferenceJsonContext.Default.LlmHostedToolCall;
    public static JsonTypeInfo<LlmHostedToolCallStartedEvent> LlmHostedToolCallStartedEvent => InferenceJsonContext.Default.LlmHostedToolCallStartedEvent;
    public static JsonTypeInfo<LlmHostedToolCallProgressEvent> LlmHostedToolCallProgressEvent => InferenceJsonContext.Default.LlmHostedToolCallProgressEvent;
    public static JsonTypeInfo<LlmHostedToolCallCompletedEvent> LlmHostedToolCallCompletedEvent => InferenceJsonContext.Default.LlmHostedToolCallCompletedEvent;
    public static JsonTypeInfo<LlmCitation> LlmCitation => InferenceJsonContext.Default.LlmCitation;
    public static JsonTypeInfo<LlmCitationEvent> LlmCitationEvent => InferenceJsonContext.Default.LlmCitationEvent;
    public static JsonTypeInfo<LlmCompletionEvent> Completion => InferenceJsonContext.Default.LlmCompletionEvent;
    public static JsonTypeInfo<DecisionQuestion> DecisionQuestion => InferenceJsonContext.Default.DecisionQuestion;
    public static JsonTypeInfo<DecisionAnswer> DecisionAnswer => InferenceJsonContext.Default.DecisionAnswer;
    public static JsonTypeInfo<DecisionInferenceRequest> DecisionRequest => InferenceJsonContext.Default.DecisionInferenceRequest;
    public static JsonTypeInfo<DecisionInferenceResult> DecisionResult => InferenceJsonContext.Default.DecisionInferenceResult;
    public static JsonTypeInfo<DecisionCapabilities> DecisionCapabilities => InferenceJsonContext.Default.DecisionCapabilities;
    public static JsonTypeInfo<DecisionInputMeasurement> DecisionMeasurement => InferenceJsonContext.Default.DecisionInputMeasurement;
    public static JsonTypeInfo<ImageGenerationRequest> ImageRequest => InferenceJsonContext.Default.ImageGenerationRequest;
    public static JsonTypeInfo<ImageGenerationResult> ImageResult => InferenceJsonContext.Default.ImageGenerationResult;
    public static JsonTypeInfo<ImageGenerationProgress> ImageProgress => InferenceJsonContext.Default.ImageGenerationProgress;
    public static JsonTypeInfo<SetupStatus> SetupStatus => InferenceJsonContext.Default.SetupStatus;
    public static JsonTypeInfo<SetupValidation> SetupValidation => InferenceJsonContext.Default.SetupValidation;
    public static JsonTypeInfo<SetupConfigurationIdentity> SetupIdentity => InferenceJsonContext.Default.SetupConfigurationIdentity;
    public static JsonTypeInfo<SetupCommitResult> SetupCommit => InferenceJsonContext.Default.SetupCommitResult;
    public static JsonTypeInfo<SetupEditResult> SetupEdit => InferenceJsonContext.Default.SetupEditResult;
    public static JsonTypeInfo<SetupActionDescriptor> SetupAction => InferenceJsonContext.Default.SetupActionDescriptor;
    public static JsonTypeInfo<SetupProgress> SetupProgress => InferenceJsonContext.Default.SetupProgress;
    public static JsonTypeInfo<SetupChoicePage> SetupChoices => InferenceJsonContext.Default.SetupChoicePage;
    public static JsonTypeInfo<InferenceResourceReference> InferenceResourceReference => InferenceJsonContext.Default.InferenceResourceReference;
    public static JsonTypeInfo<InferenceResourceDescriptor> InferenceResourceDescriptor => InferenceJsonContext.Default.InferenceResourceDescriptor;
    public static JsonTypeInfo<InferenceResourceCreate> InferenceResourceCreate => InferenceJsonContext.Default.InferenceResourceCreate;
    public static JsonTypeInfo<InferenceResourceSelectionVersion> InferenceResourceSelectionVersion => InferenceJsonContext.Default.InferenceResourceSelectionVersion;
    public static JsonTypeInfo<InferenceResourceSelection> InferenceResourceSelection => InferenceJsonContext.Default.InferenceResourceSelection;
    public static JsonTypeInfo<InferenceResourceCommitOptions> InferenceResourceCommitOptions => InferenceJsonContext.Default.InferenceResourceCommitOptions;
    public static JsonTypeInfo<InferenceResourceCommitResult> InferenceResourceCommitResult => InferenceJsonContext.Default.InferenceResourceCommitResult;
    public static JsonTypeInfo<InferenceResourceOperationPhase> InferenceResourceOperationPhase => InferenceJsonContext.Default.InferenceResourceOperationPhase;
    public static JsonTypeInfo<InferenceResourceCommitOutcome> InferenceResourceCommitOutcome => InferenceJsonContext.Default.InferenceResourceCommitOutcome;
    public static JsonTypeInfo<InferenceResourceOperationFailure> InferenceResourceOperationFailure => InferenceJsonContext.Default.InferenceResourceOperationFailure;
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(ResourceAccessJsonConverter), typeof(ResourceReadHandleJsonConverter), typeof(ResourceWriteHandleJsonConverter), typeof(ProviderAuthorizationJsonConverter)])]
[JsonSerializable(typeof(InferenceProvenance))]
[JsonSerializable(typeof(LlmInferenceRequest))]
[JsonSerializable(typeof(LlmInferenceResult))]
[JsonSerializable(typeof(LlmMessage))]
[JsonSerializable(typeof(LlmContinuationState))]
[JsonSerializable(typeof(LlmCapabilities))]
[JsonSerializable(typeof(LlmModelInfo))]
[JsonSerializable(typeof(LlmToolDefinition))]
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
