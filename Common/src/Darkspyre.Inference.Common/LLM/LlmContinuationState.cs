using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>Opaque assistant continuation content. Only the owning implementation may interpret it.
/// Preserve unchanged with its message. Treat as sensitive conversation data, never as display text.
/// Binding checks compatibility, not authenticity; imported payloads remain untrusted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmContinuationState
{
    [JsonRequired]
    public int FormatVersion { get; init; } = 2;

    public required string ImplementationId { get; init; }

    public required string ModelId { get; init; }

    public required string ScopeId { get; init; }

    /// <summary>Allowlisted continuation content only; never credentials or raw response diagnostics.</summary>
    public required string Payload { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public override string ToString() => "LlmContinuationState { opaque }";
}

/// <summary>Explicit policy for state the selected implementation cannot replay.</summary>
public enum LlmUnboundContinuationState
{
    Reject,
    /// <summary>Omit incompatible state, preserve visible history, and report the omission count.</summary>
    Omit
}
