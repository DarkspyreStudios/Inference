namespace Darkspyre.Inference.Connect.LLM;

/// <summary>One thing Connect has learned about a model on a connection.</summary>
/// <param name="ModelId">The model the fact belongs to.</param>
/// <param name="Name">What was learned, such as "Framing tokens".</param>
/// <param name="Value">The learned value as plain text.</param>
/// <param name="LearnedAt">When the value last changed in this process.</param>
public sealed record LlmLearnedModelFact(string ModelId, string Name, string Value, DateTimeOffset? LearnedAt);

/// <summary>Reads what Connect has learned about a connection's models. Makes no request.</summary>
/// <gotcha>Only facts learned since the process started are listed. A context limit saved on the connection by an earlier
/// process is not.</gotcha>
public interface ILlmLearnedModelFacts
{
    /// <summary>The facts learned for the connection's models: the estimated-to-reported token ratio, the framing
    /// tokens, the learned context limit and each refused setting, each with the time it was learned. A snapshot; an
    /// unknown connection has none.</summary>
    /// <param name="connectionId">The connection's id.</param>
    IReadOnlyList<LlmLearnedModelFact> Read(string connectionId);
}
