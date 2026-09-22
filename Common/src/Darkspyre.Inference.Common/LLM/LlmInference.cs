using System.Runtime.CompilerServices;

namespace Darkspyre.Inference.Common.LLM;

public sealed record LlmCapabilities
{
    public bool SupportsStreaming { get; init; }

    public bool SupportsModelListing { get; init; }

    public bool SupportsJsonSchema { get; init; }

    public bool SupportsJsonObject { get; init; }

    public bool SupportsReasoningEffort { get; init; }

    public bool SupportsTemperature { get; init; }

    public bool SupportsToolUse { get; init; }

    public bool SupportsPromptImages { get; init; }

    public bool SupportsPromptDocuments { get; init; }
}

public sealed record LlmModelInfo(
    string Id,
    string DisplayName,
    string? OwnedBy = null,
    LlmCapabilities? Capabilities = null)
{
    public long? MaxContextTokens { get; init; }

    public long? MaxOutputTokens { get; init; }
}

public abstract record LlmStreamEvent;

public sealed record LlmTextDeltaEvent(string Delta) : LlmStreamEvent;

public sealed record LlmReasoningDeltaEvent(string Delta, bool IsSummary) : LlmStreamEvent;

public sealed record LlmRefusalDeltaEvent(string Delta) : LlmStreamEvent;

public sealed record LlmActivityEvent(string Activity) : LlmStreamEvent;

public sealed record LlmToolCallStartedEvent(string ToolCallId, string Name, int Index) : LlmStreamEvent;

public sealed record LlmToolCallArgumentsDeltaEvent(string ToolCallId, string Delta) : LlmStreamEvent;

public sealed record LlmToolCallCompletedEvent(LlmToolCall ToolCall) : LlmStreamEvent;

public sealed record LlmCompletionEvent(LlmInferenceResult Result) : LlmStreamEvent;

public interface ILlmInference
{
    InferenceImplementationDescriptor Descriptor { get; }

    Task<LlmCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmInferenceRequest request,
        CancellationToken cancellationToken = default);
}

public static class LlmInferenceExtensions
{
    public static async Task<LlmInferenceResult> CompleteAsync(
        this ILlmInference inference,
        LlmInferenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inference);
        LlmInferenceResult? result = null;
        await foreach (LlmStreamEvent streamEvent in inference.StreamAsync(request, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (streamEvent is not LlmCompletionEvent completion)
            {
                continue;
            }

            if (result is not null)
            {
                throw new InvalidOperationException("The inference implementation emitted multiple completion events.");
            }

            result = completion.Result;
        }

        return result ?? throw new InvalidOperationException(
            "The inference implementation ended without a completion event.");
    }

    public static async IAsyncEnumerable<LlmStreamEvent> CompleteAsStreamAsync(
        LlmInferenceResult result,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new LlmCompletionEvent(result);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
