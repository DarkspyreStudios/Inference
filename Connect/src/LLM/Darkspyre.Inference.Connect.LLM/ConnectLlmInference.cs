using System.Net;
using System.Runtime.CompilerServices;
using Common = Darkspyre.Inference.Common;
using CommonLlm = Darkspyre.Inference.Common.LLM;

namespace Darkspyre.Inference.Connect.LLM;

internal sealed class ConnectLlmInference(
    LlmProviderService service,
    string connectionId) : CommonLlm.ILlmInference
{
    public Common.InferenceImplementationDescriptor Descriptor { get; } = new(
        "darkspyre.connect.llm",
        "Darkspyre Connect LLM",
        Common.InferenceImplementationKind.Connect, Common.InferenceTask.TextGeneration);

    public async Task<CommonLlm.LlmCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        LlmProviderCapabilities capabilities = modelId is null
            ? await service.GetCapabilitiesAsync(connectionId, cancellationToken).ConfigureAwait(false)
            : await service.GetModelCapabilitiesAsync(connectionId, modelId, cancellationToken).ConfigureAwait(false);
        return ToCommon(capabilities);
    }

    public async Task<IReadOnlyList<CommonLlm.LlmModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LlmModel> models = await service.ListModelsAsync(connectionId, cancellationToken)
            .ConfigureAwait(false);
        return models.Select(static model => new CommonLlm.LlmModelInfo(
            model.Id,
            model.DisplayName,
            model.OwnedBy,
            model.Capabilities is null ? null : ToCommon(model.Capabilities))
        {
            MaxContextTokens = model.MaxContextTokens,
            MaxOutputTokens = model.MaxOutputTokens
        }).ToArray();
    }

    public async IAsyncEnumerable<CommonLlm.LlmStreamEvent> StreamAsync(
        CommonLlm.LlmInferenceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IAsyncEnumerable<LlmStreamEvent> stream;
        try
        {
            stream = service.StreamAsync(connectionId, ToConnect(request), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ToCommonException(exception);
        }

        IAsyncEnumerator<LlmStreamEvent> enumerator = stream.GetAsyncEnumerator(cancellationToken);
        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw ToCommonException(exception);
                }

                if (!hasNext)
                {
                    yield break;
                }

                yield return ToCommon(enumerator.Current, request.ModelId);
            }
        }
    }

    private CommonLlm.LlmStreamEvent ToCommon(LlmStreamEvent streamEvent, string modelId) => streamEvent switch
    {
        LlmTextDeltaEvent value => new CommonLlm.LlmTextDeltaEvent(value.Delta),
        LlmReasoningDeltaEvent value => new CommonLlm.LlmReasoningDeltaEvent(value.Delta, value.IsSummary),
        LlmRefusalDeltaEvent value => new CommonLlm.LlmRefusalDeltaEvent(value.Delta),
        LlmActivityEvent value => new CommonLlm.LlmActivityEvent(value.Activity),
        LlmToolCallStartedEvent value => new CommonLlm.LlmToolCallStartedEvent(
            value.ToolCallId,
            value.Name,
            value.Index),
        LlmToolCallArgumentsDeltaEvent value => new CommonLlm.LlmToolCallArgumentsDeltaEvent(
            value.ToolCallId,
            value.Delta),
        LlmToolCallCompletedEvent value => new CommonLlm.LlmToolCallCompletedEvent(ToCommon(value.ToolCall)),
        LlmCompletionEvent value => new CommonLlm.LlmCompletionEvent(ToCommon(value.Result, modelId)),
        _ => throw new Common.InferenceException(
            Common.InferenceFailureCode.ExecutionFailed,
            $"Connect emitted unsupported stream event '{streamEvent.GetType().Name}'.")
    };

    private CommonLlm.LlmInferenceResult ToCommon(LlmChatResult result, string modelId) => new()
    {
        Text = result.Text,
        Refusal = result.Refusal,
        RefusalCategory = result.RefusalCategory,
        Status = result.Status switch
        {
            LlmCompletionStatus.Completed => CommonLlm.LlmCompletionStatus.Completed,
            LlmCompletionStatus.Incomplete => CommonLlm.LlmCompletionStatus.Incomplete,
            LlmCompletionStatus.Refused => CommonLlm.LlmCompletionStatus.Refused,
            LlmCompletionStatus.Failed => CommonLlm.LlmCompletionStatus.Failed,
            LlmCompletionStatus.RequiresAction => CommonLlm.LlmCompletionStatus.RequiresAction,
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        },
        FinishReason = result.FinishReason,
        IncompleteReason = result.IncompleteReason,
        Usage = result.Usage is null
            ? null
            : new CommonLlm.LlmUsage(
                result.Usage.InputTokens,
                result.Usage.OutputTokens,
                result.Usage.TotalTokens),
        ToolCalls = result.ToolCalls.Select(ToCommon).ToArray(),
        StructuredOutput = result.StructuredOutput is null
            ? null
            : new CommonLlm.LlmStructuredOutputValidation(
                result.StructuredOutput.Enforcement switch
                {
                    LlmStructuredOutputEnforcement.ProviderJsonSchema =>
                        CommonLlm.LlmStructuredOutputEnforcement.NativeJsonSchema,
                    LlmStructuredOutputEnforcement.ProviderJsonObject =>
                        CommonLlm.LlmStructuredOutputEnforcement.NativeJsonObject,
                    LlmStructuredOutputEnforcement.LocalValidationOnly =>
                        CommonLlm.LlmStructuredOutputEnforcement.LocalValidationOnly,
                    _ => throw new ArgumentOutOfRangeException(nameof(result))
                },
                result.StructuredOutput.WasEvaluated,
                result.StructuredOutput.IsValid,
                result.StructuredOutput.Errors),
        Provenance = new Common.InferenceProvenance
        {
            ImplementationId = Descriptor.Id,
            ModelId = modelId,
            Runtime = "Darkspyre.Inference.Connect.LLM",
            Backend = "connect"
        }
    };

    private static LlmChatRequest ToConnect(CommonLlm.LlmInferenceRequest request) => new()
    {
        ModelId = request.ModelId,
        Instructions = request.Instructions,
        Messages = request.Messages.Select(static message => new LlmChatMessage(message.Role, message.Content)
        {
            Attachments = message.Attachments.Select(static attachment => new LlmAttachment
            {
                Kind = attachment.Kind switch
                {
                    CommonLlm.LlmAttachmentKind.Image => LlmAttachmentKind.Image,
                    CommonLlm.LlmAttachmentKind.Document => LlmAttachmentKind.Document,
                    _ => throw new ArgumentOutOfRangeException(nameof(attachment))
                },
                FileName = attachment.FileName,
                Data = attachment.Data
            }).ToArray(),
            ToolCalls = message.ToolCalls.Select(static call => new LlmToolCall(
                call.Id,
                call.Name,
                call.ArgumentsJson)).ToArray(),
            ToolResults = message.ToolResults.Select(static result => new LlmToolResult(
                result.ToolCallId,
                result.Content,
                result.IsError)
            {
                Attachments = result.Attachments.Select(static attachment => new LlmAttachment
                {
                    Kind = attachment.Kind switch
                    {
                        CommonLlm.LlmAttachmentKind.Image => LlmAttachmentKind.Image,
                        CommonLlm.LlmAttachmentKind.Document => LlmAttachmentKind.Document,
                        _ => throw new ArgumentOutOfRangeException(nameof(attachment))
                    },
                    FileName = attachment.FileName,
                    Data = attachment.Data
                }).ToArray()
            }).ToArray()
        }).ToArray(),
        Temperature = request.Temperature,
        MaxOutputTokens = request.MaxOutputTokens,
        ReasoningLevel = request.ReasoningLevel,
        StructuredOutput = request.StructuredOutput is null
            ? null
            : new LlmStructuredOutputRequest
            {
                Name = request.StructuredOutput.Name,
                Description = request.StructuredOutput.Description,
                Schema = request.StructuredOutput.Schema,
                Strict = request.StructuredOutput.Strict,
                Fallback = request.StructuredOutput.Fallback switch
                {
                    CommonLlm.LlmStructuredOutputFallback.None => LlmStructuredOutputFallback.None,
                    CommonLlm.LlmStructuredOutputFallback.JsonObject => LlmStructuredOutputFallback.JsonObject,
                    CommonLlm.LlmStructuredOutputFallback.LocalValidation => LlmStructuredOutputFallback.LocalValidation,
                    _ => throw new ArgumentOutOfRangeException(nameof(request))
                }
            },
        Tools = request.Tools.Select(static tool => new LlmToolDefinition
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.InputSchema,
            Strict = tool.Strict
        }).ToArray(),
        ToolChoice = request.ToolChoice is null
            ? null
            : new LlmToolChoice
            {
                Mode = request.ToolChoice.Mode switch
                {
                    CommonLlm.LlmToolChoiceMode.Auto => LlmToolChoiceMode.Auto,
                    CommonLlm.LlmToolChoiceMode.None => LlmToolChoiceMode.None,
                    CommonLlm.LlmToolChoiceMode.Required => LlmToolChoiceMode.Required,
                    CommonLlm.LlmToolChoiceMode.Specific => LlmToolChoiceMode.Specific,
                    _ => throw new ArgumentOutOfRangeException(nameof(request))
                },
                Name = request.ToolChoice.Name
            },
        AllowParallelToolCalls = request.AllowParallelToolCalls,
        ConversationKey = request.ConversationKey
    };

    private static CommonLlm.LlmToolCall ToCommon(LlmToolCall call) =>
        new(call.Id, call.Name, call.ArgumentsJson);

    private static CommonLlm.LlmCapabilities ToCommon(LlmProviderCapabilities capabilities) => new()
    {
        OutputLimitEnforcement = capabilities.OutputLimitEnforcement,
        SupportsStreaming = capabilities.SupportsStreaming,
        SupportsModelListing = capabilities.SupportsModelListing,
        SupportsJsonSchema = capabilities.SupportsJsonSchema,
        SupportsJsonObject = capabilities.SupportsJsonObject,
        ReasoningLevels = capabilities.ReasoningLevels,
        SupportsTemperature = capabilities.SupportsTemperature,
        SupportsToolUse = capabilities.SupportsToolUse,
        SupportsPromptImages = capabilities.SupportsPromptImages,
        SupportsPromptDocuments = capabilities.SupportsPromptDocuments
    };

    private static Common.InferenceException ToCommonException(Exception exception) => exception switch
    {
        Common.InferenceException inference => inference,
        NotSupportedException => new Common.InferenceException(
            Common.InferenceFailureCode.UnsupportedFeature,
            exception.Message,
            exception),
        LlmProviderException provider when provider.StatusCode == HttpStatusCode.TooManyRequests =>
            new Common.InferenceException(Common.InferenceFailureCode.CapacityExhausted, provider.Message, provider),
        _ => new Common.InferenceException(Common.InferenceFailureCode.ExecutionFailed, exception.Message, exception)
    };
}
