using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Darkspyre.Inference.Common.Decision;
using Darkspyre.Inference.Common.Image;
using Darkspyre.Inference.Common.LLM;
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
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
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
internal sealed partial class InferenceJsonContext : JsonSerializerContext;
