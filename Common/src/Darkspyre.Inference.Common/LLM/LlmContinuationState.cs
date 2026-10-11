using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Common.LLM;

/// <summary>Opaque assistant continuation content. Only the owning implementation may interpret it.
/// Preserve unchanged with its message. Treat as sensitive conversation data, never as display text.
/// Binding checks compatibility, not authenticity; imported payloads remain untrusted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmContinuationState
{
    /// <summary>The version of the continuation state format.</summary>
    [JsonRequired]
    public int FormatVersion { get; init; } = 2;

    /// <summary>The implementation that produced the state and alone may interpret it.</summary>
    public required string ImplementationId { get; init; }

    /// <summary>The model the state was produced for.</summary>
    public required string ModelId { get; init; }

    /// <summary>Identifies the connection scope the state was produced under. Replay requires a matching scope.</summary>
    public required string ScopeId { get; init; }

    /// <summary>Allowlisted continuation content only; never credentials or raw response diagnostics.</summary>
    public required string Payload { get; init; }

    /// <summary>The input token count reported for the turn that produced the state, or null when unknown.</summary>
    public long? InputTokens { get; init; }

    /// <summary>The output token count reported for the turn that produced the state, or null when unknown.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>Returns a fixed placeholder so the opaque payload never reaches logs or display text.</summary>
    public override string ToString() => "LlmContinuationState { opaque }";
}

/// <summary>Explicit policy for state the selected implementation cannot replay.</summary>
public enum LlmUnboundContinuationState
{
    /// <summary>Refuse the request when it carries incompatible state.</summary>
    Reject,
    /// <summary>Omit incompatible state, preserve visible history, and report the omission count.</summary>
    Omit
}
