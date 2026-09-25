using System.Net;
using System.Runtime.CompilerServices;
using Common = Darkspyre.Inference.Common;
using CommonLlm = Darkspyre.Inference.Common.LLM;
using Darkspyre.Inference.Connect.LLM.Internal;

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
        CancellationToken cancellationToken = default) =>
        ToCommon(await service.ListModelsAsync(connectionId, cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<CommonLlm.LlmModelInfo>> RefreshModelsAsync(
        CancellationToken cancellationToken = default) =>
        ToCommon(await service.RefreshModelsAsync(connectionId, cancellationToken).ConfigureAwait(false));

    private static CommonLlm.LlmModelInfo[] ToCommon(IReadOnlyList<LlmModel> models) =>
        models.Select(static model => new CommonLlm.LlmModelInfo(
            model.Id,
            model.DisplayName,
            model.OwnedBy,
            model.Capabilities is null ? null : ToCommon(model.Capabilities))
        {
            MaxContextTokens = model.MaxContextTokens,
            MaxOutputTokens = model.MaxOutputTokens
        }).ToArray();

    public async IAsyncEnumerable<CommonLlm.LlmStreamEvent> StreamAsync(
        CommonLlm.LlmInferenceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IAsyncEnumerable<CommonLlm.LlmStreamEvent> stream;
        try
        {
            stream = service.StreamAsync(connectionId, ToConnect(request), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ToCommonException(exception);
        }

        IAsyncEnumerator<CommonLlm.LlmStreamEvent> enumerator = stream.GetAsyncEnumerator(cancellationToken);
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

    // Connect streams Common's events; only its completion event carries a Connect-only result.
    private CommonLlm.LlmStreamEvent ToCommon(CommonLlm.LlmStreamEvent streamEvent, string modelId) =>
        streamEvent is LlmChatCompletionEvent completion
            ? new CommonLlm.LlmCompletionEvent(ToCommon(completion.Result, modelId))
            : streamEvent;

    private CommonLlm.LlmInferenceResult ToCommon(LlmChatResult result, string modelId) => new()
    {
        Text = result.Text,
        Refusal = result.Refusal,
        RefusalCategory = result.RefusalCategory,
        Status = result.Status,
        FinishReason = result.FinishReason,
        IncompleteReason = result.IncompleteReason,
        Usage = result.Usage,
        ToolCalls = result.ToolCalls,
        StructuredOutput = result.StructuredOutput,
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
            Attachments = message.Attachments,
            ToolCalls = message.ToolCalls,
            ToolResults = message.ToolResults
        }).ToArray(),
        Temperature = request.Temperature,
        MaxOutputTokens = request.MaxOutputTokens,
        ReasoningLevel = request.ReasoningLevel,
        StructuredOutput = request.StructuredOutput,
        Tools = request.Tools,
        ToolChoice = request.ToolChoice,
        AllowParallelToolCalls = request.AllowParallelToolCalls,
        ConversationKey = request.ConversationKey
    };

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
