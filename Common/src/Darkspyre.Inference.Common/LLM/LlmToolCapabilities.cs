using Darkspyre.Inference.Common;
using System.Collections.Frozen;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>Optional controls beyond ordinary tool calling. Combinations may have additional model constraints.</summary>
public sealed record LlmToolCapabilities
{
    public IReadOnlySet<LlmToolChoiceMode> ChoiceModes { get; init; } = FrozenSet<LlmToolChoiceMode>.Empty;
    public InferenceSupport SupportsParallelCalls { get; init; }
    public InferenceSupport SupportsParallelCallControl { get; init; }
    public InferenceSupport SupportsArgumentStreaming { get; init; }
}

/// <summary>Evidence of permission/availability for this connection, distinct from model support.</summary>
public enum LlmModelAvailability
{
    Unknown,
    Available,
    Unavailable
}
