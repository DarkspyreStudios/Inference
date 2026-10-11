using Darkspyre.Inference.Common;
using System.Collections.Frozen;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>Optional controls beyond ordinary tool calling. Combinations may have additional model constraints.</summary>
public sealed record LlmToolCapabilities
{
    /// <summary>The tool choice modes the model accepts.</summary>
    public IReadOnlySet<LlmToolChoiceMode> ChoiceModes { get; init; } = FrozenSet<LlmToolChoiceMode>.Empty;
    /// <summary>Whether the model can make several tool calls in one turn.</summary>
    public InferenceSupport SupportsParallelCalls { get; init; }
    /// <summary>Whether the caller can turn parallel tool calls on or off.</summary>
    public InferenceSupport SupportsParallelCallControl { get; init; }
    /// <summary>Whether tool call arguments stream incrementally.</summary>
    public InferenceSupport SupportsArgumentStreaming { get; init; }
}

/// <summary>Evidence of permission/availability for this connection, distinct from model support.</summary>
public enum LlmModelAvailability
{
    /// <summary>No availability evidence exists.</summary>
    Unknown,
    /// <summary>The connection can use the model.</summary>
    Available,
    /// <summary>The connection cannot use the model.</summary>
    Unavailable
}
