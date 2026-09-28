using System.Collections.Frozen;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>Optional controls beyond ordinary non-strict tool calling. Combinations may have
/// additional model constraints. Unsupported strict enforcement never disables ordinary tools.</summary>
public sealed record LlmToolCapabilities
{
    public IReadOnlySet<LlmToolChoiceMode> ChoiceModes { get; init; } = FrozenSet<LlmToolChoiceMode>.Empty;
    /// <summary>Unknown permits an explicit strict request; the host decides whether to attempt it.</summary>
    public LlmStrictInputSchemaSupport StrictInputSchema { get; init; }
    public bool SupportsParallelCalls { get; init; }
    public bool SupportsParallelCallControl { get; init; }
    public bool SupportsArgumentStreaming { get; init; }
}

/// <summary>Evidence of permission/availability for this connection, distinct from model support.</summary>
public enum LlmModelAvailability
{
    Unknown,
    Available,
    Unavailable
}

/// <summary>Evidence of strict argument-schema enforcement, independently of ordinary tool support.</summary>
public enum LlmStrictInputSchemaSupport
{
    /// <summary>Unverified support. Connect permits the host's explicit strict request without downgrading it.</summary>
    Unknown,
    /// <summary>The adapter/model supports provider-enforced tool argument schemas.</summary>
    Supported,
    /// <summary>The adapter/model cannot honor strict tool argument schemas; explicit strict requests fail.</summary>
    Unsupported
}
