using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>
/// How much reasoning a request asks of the model that runs it. The order is the only guarantee across
/// providers: on one model, each level asks for at least as much reasoning as the level below it. A
/// level does not cost the same compute on two different models.
/// </summary>
public enum LlmReasoningLevel
{
    /// <summary>No reasoning, where the model can switch it off.</summary>
    None,
    Minimal,
    Low,
    Medium,
    High,
    ExtraHigh,
    Maximum
}

/// <summary>Who enforces a requested output-token cap. A planning reserve is not enforcement.</summary>
public enum LlmOutputLimitEnforcement
{
    Unknown,
    Unsupported,
    ProviderEnforced,
    EngineEnforced
}

public sealed record LlmCapabilities
{
    public LlmOutputLimitEnforcement OutputLimitEnforcement { get; init; }

    /// <summary>
    /// True when <see cref="ILlmInference.StreamAsync"/> delivers events while the model generates. False when the
    /// events arrive together after generation ends.
    /// </summary>
    public bool SupportsStreaming { get; init; }

    /// <summary>A streamed response can report final token usage. Independent of running usage observations.</summary>
    public bool SupportsStreamingUsage { get; init; }

    /// <summary>A streamed response can report cumulative usage before completion.</summary>
    public bool SupportsUsageDeltas { get; init; }

    /// <summary>Public reasoning text can arrive separately from the answer. Independent of reasoning-level control.</summary>
    public bool SupportsReasoningText { get; init; }

    public bool SupportsModelListing { get; init; }

    public bool SupportsJsonSchema { get; init; }

    public bool SupportsJsonObject { get; init; }

    /// <summary>The reasoning levels the model accepts. Empty when the model accepts no reasoning control.</summary>
    public IReadOnlySet<LlmReasoningLevel> ReasoningLevels { get; init; } = FrozenSet<LlmReasoningLevel>.Empty;

    /// <summary>True when the model accepts a reasoning level above <see cref="LlmReasoningLevel.None"/>.</summary>
    public bool SupportsReasoning => ReasoningLevels.Any(static level => level > LlmReasoningLevel.None);

    public bool SupportsTemperature { get; init; }

    public bool SupportsToolUse { get; init; }

    public LlmToolCapabilities Tools { get; init; } = new();

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

    public LlmModelAvailability Availability { get; init; }

    public DateTimeOffset? AvailabilityCheckedAt { get; init; }

    public string? AvailabilityReason { get; init; }
}

public abstract record LlmStreamEvent;

public sealed record LlmTextDeltaEvent(string Delta) : LlmStreamEvent;

/// <summary>Replaces the previous partial structured object. Only the terminal result is schema-validated.</summary>
public sealed record LlmStructuredOutputSnapshotEvent(JsonElement Content) : LlmStreamEvent;

/// <summary>Provider-exposed reasoning text only; never encrypted or private response-state bytes.</summary>
public sealed record LlmReasoningDeltaEvent(string Delta, bool IsSummary) : LlmStreamEvent;

public sealed record LlmRefusalDeltaEvent(string Delta) : LlmStreamEvent;

public sealed record LlmActivityEvent(string Activity) : LlmStreamEvent;

public sealed record LlmToolCallStartedEvent(string ToolCallId, string Name, int Index) : LlmStreamEvent;

public sealed record LlmToolCallArgumentsDeltaEvent(string ToolCallId, string Delta) : LlmStreamEvent;

public sealed record LlmToolCallCompletedEvent(LlmToolCall ToolCall) : LlmStreamEvent;

/// <summary>
/// Running usage for the entire call. Each observation replaces the previous one; never add observations together.
/// Null fields are unreported. Completion usage is authoritative and can differ from the last observation.
/// </summary>
public sealed record LlmUsageDeltaEvent(LlmUsage Cumulative) : LlmStreamEvent;

public sealed record LlmCompletionEvent(LlmInferenceResult Result) : LlmStreamEvent;

public interface ILlmInference
{
    InferenceImplementationDescriptor Descriptor { get; }

    Task<LlmCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default);

    /// <summary>The available models. An implementation may keep the list and answer later calls from it.</summary>
    Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the available models again, replacing any list the implementation keeps.</summary>
    Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default);

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
