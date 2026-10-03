using System.Text.Json.Serialization;

namespace Darkspyre.Inference.Connect.LLM;

/// <summary>Requests a provider-created replacement proposal for the host to commit separately.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LlmProviderCompactionRequest
{
    public string? PauseId { get; init; }

    public string? TurnId { get; init; }

    [JsonRequired]
    public long ExpectedContextRevision { get; init; }

    /// <summary>Budget for older user entries; paused protected records are retained independently.</summary>
    [JsonRequired]
    public long RetainedUserTokens { get; init; }
}
