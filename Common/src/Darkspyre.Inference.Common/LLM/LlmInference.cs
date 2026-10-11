using Darkspyre.Inference.Common;
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
    /// <summary>The least reasoning above none.</summary>
    Minimal,
    /// <summary>Low reasoning.</summary>
    Low,
    /// <summary>Medium reasoning.</summary>
    Medium,
    /// <summary>High reasoning.</summary>
    High,
    /// <summary>Reasoning above high.</summary>
    ExtraHigh,
    /// <summary>The most reasoning the model offers.</summary>
    Maximum
}

/// <summary>Who enforces a requested output-token cap. A planning reserve is not enforcement.</summary>
public enum LlmOutputLimitEnforcement
{
    /// <summary>No evidence of who enforces the cap.</summary>
    Unknown,
    /// <summary>Nothing enforces the cap.</summary>
    Unsupported,
    /// <summary>The provider stops output at the cap.</summary>
    ProviderEnforced,
    /// <summary>The local engine stops output at the cap.</summary>
    EngineEnforced
}

/// <summary>What a model and its route support for LLM requests.</summary>
public sealed record LlmCapabilities
{
    /// <summary>Who enforces a requested output-token cap.</summary>
    public LlmOutputLimitEnforcement OutputLimitEnforcement { get; init; }

    /// <summary>
    /// Supported means <see cref="ILlmInference.StreamAsync"/> delivers events while the model generates.
    /// Unsupported means the events arrive together after generation ends. Unknown leaves timing unqualified.
    /// </summary>
    public InferenceSupport SupportsStreaming { get; init; }

    /// <summary>A streamed response can report final token usage. Independent of running usage observations.</summary>
    public InferenceSupport SupportsStreamingUsage { get; init; }

    /// <summary>A streamed response can report cumulative usage before completion.</summary>
    public InferenceSupport SupportsUsageDeltas { get; init; }

    /// <summary>Public reasoning text can arrive separately from the answer. Independent of reasoning-level control.</summary>
    public InferenceSupport SupportsReasoningText { get; init; }

    /// <summary>Support for listing the available models.</summary>
    public InferenceSupport SupportsModelListing { get; init; }

    /// <summary>Support for output constrained to a JSON Schema.</summary>
    public InferenceSupport SupportsJsonSchema { get; init; }

    /// <summary>Support for output constrained to any JSON object.</summary>
    public InferenceSupport SupportsJsonObject { get; init; }

    /// <summary>The prompt caching the route offers.</summary>
    public LlmPromptCacheCapabilities PromptCache { get; init; } = new();

    /// <summary>The selected route implements explicit native cache selection.</summary>
    public InferenceSupport SupportsExplicitCache { get; init; }

    /// <summary>The reasoning levels the model accepts. Empty when the model accepts no reasoning control.</summary>
    public IReadOnlySet<LlmReasoningLevel> ReasoningLevels { get; init; } = FrozenSet<LlmReasoningLevel>.Empty;

    /// <summary>Support for reasoning above <see cref="LlmReasoningLevel.None"/>. An empty level set leaves support unknown.</summary>
    public InferenceSupport SupportsReasoning => ReasoningLevels.Count == 0 ? InferenceSupport.Unknown
        : ReasoningLevels.Any(static level => level > LlmReasoningLevel.None) ? InferenceSupport.Supported : InferenceSupport.Unsupported;

    /// <summary>Support for the sampling temperature setting.</summary>
    public InferenceSupport SupportsTemperature { get; init; }

    /// <summary>Support for nucleus (top-p) sampling.</summary>
    public InferenceSupport SupportsTopP { get; init; }

    /// <summary>Support for top-k sampling.</summary>
    public InferenceSupport SupportsTopK { get; init; }

    /// <summary>Support for a sampling seed.</summary>
    public InferenceSupport SupportsSeed { get; init; }

    /// <summary>Support for a presence penalty.</summary>
    public InferenceSupport SupportsPresencePenalty { get; init; }

    /// <summary>Support for a frequency penalty.</summary>
    public InferenceSupport SupportsFrequencyPenalty { get; init; }

    /// <summary>Zero disables string stops; int.MaxValue means no adapter-imposed limit.</summary>
    public int MaxStopSequences { get; init; }

    /// <summary>Support for calling caller-defined tools.</summary>
    public InferenceSupport SupportsToolUse { get; init; }

    /// <summary>The route implements shell definitions, calls and typed results.</summary>
    public InferenceSupport SupportsShellTools { get; init; }

    /// <summary>The details of the route's tool-calling support.</summary>
    public LlmToolCapabilities Tools { get; init; } = new();

    /// <summary>The provider-hosted tool kinds the route offers.</summary>
    public IReadOnlySet<LlmHostedToolKind> HostedTools { get; init => field = value.ToFrozenSet(); } = FrozenSet<LlmHostedToolKind>.Empty;

    /// <summary>Support for images in the prompt.</summary>
    public InferenceSupport SupportsPromptImages { get; init; }

    /// <summary>Support for documents in the prompt.</summary>
    public InferenceSupport SupportsPromptDocuments { get; init; }

    /// <summary>The selected route accepts provider-native file references.</summary>
    public InferenceSupport SupportsFileReferences { get; init; }
}

/// <summary>A model a connection offers.</summary>
/// <param name="Id">The model identity sent in requests.</param>
/// <param name="DisplayName">The name shown to people.</param>
/// <param name="OwnedBy">The organization that publishes the model, when reported.</param>
/// <param name="Capabilities">What the model supports, when known.</param>
public sealed record LlmModelInfo(
    string Id,
    string DisplayName,
    string? OwnedBy = null,
    LlmCapabilities? Capabilities = null)
{
    /// <summary>The model's context window in tokens, when known.</summary>
    public long? MaxContextTokens { get; init; }

    /// <summary>The most output tokens the model produces per response, when known.</summary>
    public long? MaxOutputTokens { get; init; }

    /// <summary>Whether the connection can use the model.</summary>
    public LlmModelAvailability Availability { get; init; }

    /// <summary>When <see cref="Availability"/> was last checked; null when never.</summary>
    public DateTimeOffset? AvailabilityCheckedAt { get; init; }

    /// <summary>Text that explains <see cref="Availability"/>, when one exists.</summary>
    public string? AvailabilityReason { get; init; }
}

/// <summary>Base type for the events an LLM stream reports.</summary>
public abstract record LlmStreamEvent;

/// <summary>A fragment of answer text.</summary>
/// <param name="Delta">The new text.</param>
public sealed record LlmTextDeltaEvent(string Delta) : LlmStreamEvent;

/// <summary>Replaces the previous partial structured object. Only the terminal result is schema-validated.</summary>
public sealed record LlmStructuredOutputSnapshotEvent(JsonElement Content) : LlmStreamEvent;

/// <summary>Provider-exposed reasoning text only; never encrypted or private response-state bytes.</summary>
public sealed record LlmReasoningDeltaEvent(string Delta, bool IsSummary) : LlmStreamEvent;

/// <summary>A fragment of refusal text from the model.</summary>
/// <param name="Delta">The new text.</param>
public sealed record LlmRefusalDeltaEvent(string Delta) : LlmStreamEvent;

/// <summary>Implementation-owned progress text. It is not answer content.</summary>
/// <param name="Activity">The progress text.</param>
public sealed record LlmActivityEvent(string Activity) : LlmStreamEvent;

/// <summary>The model began a tool call.</summary>
/// <param name="ToolCallId">The tool call identity.</param>
/// <param name="Name">The called tool.</param>
/// <param name="Index">The position of the call in the response.</param>
public sealed record LlmToolCallStartedEvent(string ToolCallId, string Name, int Index) : LlmStreamEvent;

/// <summary>A fragment of a tool call's JSON arguments.</summary>
/// <param name="ToolCallId">The tool call identity.</param>
/// <param name="Delta">The new argument text.</param>
public sealed record LlmToolCallArgumentsDeltaEvent(string ToolCallId, string Delta) : LlmStreamEvent;

/// <summary>The model finished a tool call.</summary>
/// <param name="ToolCall">The complete call.</param>
public sealed record LlmToolCallCompletedEvent(LlmToolCall ToolCall) : LlmStreamEvent;

/// <summary>
/// Running usage for the entire call. Each observation replaces the previous one; never add observations together.
/// Null fields are unreported. Completion usage is authoritative and can differ from the last observation.
/// </summary>
public sealed record LlmUsageDeltaEvent(LlmUsage Cumulative) : LlmStreamEvent;

/// <summary>The final event of a stream, carrying the whole result.</summary>
/// <param name="Result">The completed inference result.</param>
public sealed record LlmCompletionEvent(LlmInferenceResult Result) : LlmStreamEvent;

/// <summary>Runs LLM requests against one implementation.</summary>
public interface ILlmInference
{
    /// <summary>Identifies the implementation behind this instance.</summary>
    InferenceImplementationDescriptor Descriptor { get; }

    /// <summary>Returns what the given model supports; null returns the capabilities of the implementation as a whole.</summary>
    Task<LlmCapabilities> GetCapabilitiesAsync(
        string? modelId = null,
        CancellationToken cancellationToken = default);

    /// <summary>The available models. An implementation may keep the list and answer later calls from it.</summary>
    Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the available models again, replacing any list the implementation keeps.</summary>
    Task<IReadOnlyList<LlmModelInfo>> RefreshModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a request and streams its events, ending with one <see cref="LlmCompletionEvent"/>.</summary>
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmInferenceRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Helpers over <see cref="ILlmInference"/>.</summary>
public static class LlmInferenceExtensions
{
    /// <summary>Runs a request and returns the result from its single completion event.</summary>
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

    /// <summary>Returns a stream holding only a completion event for an existing result.</summary>
    public static async IAsyncEnumerable<LlmStreamEvent> CompleteAsStreamAsync(
        LlmInferenceResult result,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new LlmCompletionEvent(result);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
